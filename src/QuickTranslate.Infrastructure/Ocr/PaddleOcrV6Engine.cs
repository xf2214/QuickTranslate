using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.AppData;

namespace QuickTranslate.Infrastructure.Ocr;

public class PaddleOcrV6Engine : IOcrEngine, IDisposable
{
    private readonly IOptions<AppSettings> _settings;
    private readonly IAppDataProvider _appDataProvider;
    private readonly ILogger<PaddleOcrV6Engine> _logger;
    private readonly bool _modelReady;
    private readonly string? _modelsDirectory;

    private Lazy<Task<InferenceSessionsHolder>>? _initTask;
    private bool _sessionCreatedRaised;
    private bool _disposed;

    public string EngineName => "PP-OCRv6-ONNX";
    public bool IsAvailable => _modelReady;

    public event EventHandler? SessionCreated;

    public PaddleOcrV6Engine(
        IOptions<AppSettings> settings,
        IAppDataProvider appDataProvider,
        ILogger<PaddleOcrV6Engine> logger)
    {
        _settings = settings;
        _appDataProvider = appDataProvider;
        _logger = logger;

        var candidateDirs = new List<string>
        {
            Path.Combine(_appDataProvider.GetAppDataDirectory(), "models"),
            Path.Combine(AppContext.BaseDirectory, "assets", "models"),
        };

        // 开发辅助：允许从本机仓库固定位置加载模型，避免每次发布时复制。
        // 测试 / CI 等无头环境可设置 QUICKTRANSLATE_DISABLE_DEV_MODEL_PATHS=1
        // 跳过本机硬编码路径，确保 MissingModels 等测试的行为可预测。
        const string disableDevEnv = "QUICKTRANSLATE_DISABLE_DEV_MODEL_PATHS";
        var skipDevPaths = Environment.GetEnvironmentVariable(disableDevEnv);
        if (!string.Equals(skipDevPaths, "1", StringComparison.Ordinal))
        {
            candidateDirs.Add(@"E:\翻译\assets\models");
        }

        foreach (var dir in candidateDirs)
        {
            if (CheckModelFiles(dir))
            {
                _modelsDirectory = dir;
                _modelReady = true;
                _logger.LogInformation("PP-OCRv6 models found in {ModelDir}", dir);
                break;
            }

            // 详细 Debug 级日志：列出每个候选路径的命中情况及缺失文件，便于用户在 DebugLogging 打开时快速定位
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                if (!Directory.Exists(dir))
                {
                    _logger.LogDebug("PP-OCRv6 model dir {Dir} skipped: directory does not exist", dir);
                }
                else
                {
                    var required = new[] { "det.onnx", "rec.onnx", "ppocr_keys.txt" };
                    var missing = required.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
                    _logger.LogDebug("PP-OCRv6 model dir {Dir} skipped: missing files = {Missing}", dir,
                        missing.Count == 0 ? "(none)" : string.Join(", ", missing));
                }
            }
        }

        if (!_modelReady)
        {
            _logger.LogWarning(
                "PP-OCRv6 models missing. Please place det.onnx/cls.onnx/rec.onnx/ppocr_keys.txt under assets/models or %APPDATA%/QuickTranslate/models");
        }

        if (_modelReady)
        {
            _initTask = new Lazy<Task<InferenceSessionsHolder>>(() => InitializeSessionsAsync(),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private static bool CheckModelFiles(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        // det.onnx, rec.onnx, ppocr_keys.txt are required; cls.onnx is optional (angle classification skipped if missing)
        var required = new[] { "det.onnx", "rec.onnx", "ppocr_keys.txt" };
        return required.All(f => File.Exists(Path.Combine(dir, f)));
    }

    private async Task<InferenceSessionsHolder> InitializeSessionsAsync()
    {
        await Task.Yield();

        var holder = new InferenceSessionsHolder();

        try
        {
            if (_modelsDirectory != null)
            {
                var detPath = Path.Combine(_modelsDirectory, "det.onnx");
                var clsPath = Path.Combine(_modelsDirectory, "cls.onnx");
                var recPath = Path.Combine(_modelsDirectory, "rec.onnx");

                var so = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
                };
                try { so.AppendExecutionProvider_CPU(0); } catch { /* ignore if EP already attached */ }

                try
                {
                    holder.DetSession = new InferenceSession(detPath, so);
                    // cls.onnx is optional; if missing or load fails, we simply skip 180deg classification
                    if (File.Exists(clsPath))
                    {
                        try { holder.ClsSession = new InferenceSession(clsPath, so); }
                        catch (Exception clsEx)
                        {
                            _logger.LogInformation(clsEx, "cls.onnx optional session skipped: {Msg}", clsEx.Message);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("cls.onnx not found; angle classification step skipped");
                    }
                    holder.RecSession = new InferenceSession(recPath, so);

                    var dictPath = Path.Combine(_modelsDirectory, "ppocr_keys.txt");
                    if (File.Exists(dictPath))
                    {
                        var lines = await File.ReadAllLinesAsync(dictPath);
                        holder.CharDictionary = BuildCharDictionary(lines);

                        // 诊断：字典长度必须等于 rec 输出类别数（blank + 字符 + 空格）。
                        // 不一致（例如 v6 模型误配 ppocr_keys_v1.txt）时 CTC 解码必然全错。
                        try
                        {
                            var outMeta = holder.RecSession.OutputMetadata.Values.First();
                            var dims = outMeta.Dimensions;
                            int classCount = dims != null && dims.Length > 0 ? dims[dims.Length - 1] : -1;
                            if (classCount > 0 && classCount != holder.CharDictionary.Length)
                            {
                                _logger.LogWarning(
                                    "OCR dictionary/model mismatch: dict labels={DictLen} but rec output classes={ClassCount}. " +
                                    "Recognition will be garbage. ppocr_keys.txt must match the rec.onnx model ({ModelDir}).",
                                    holder.CharDictionary.Length, classCount, _modelsDirectory);
                            }
                            else if (classCount > 0)
                            {
                                _logger.LogInformation(
                                    "OCR dictionary OK: {DictLen} labels = rec output classes {ClassCount}",
                                    holder.CharDictionary.Length, classCount);
                            }
                        }
                        catch (Exception metaEx)
                        {
                            _logger.LogDebug(metaEx, "Could not read rec output metadata for dictionary size check");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ONNX Runtime session creation failed; running in skeleton mode");
                }
            }
        }
        finally
        {
            if (!_sessionCreatedRaised)
            {
                _sessionCreatedRaised = true;
                _logger.LogInformation("OCR Session created");
                SessionCreated?.Invoke(this, EventArgs.Empty);
            }
        }

        return holder;
    }

    private static string[] BuildCharDictionary(string[] rawLines)
    {
        // PP-OCR CTC 标签布局（与 PaddleOCR CTCLabelDecode 一致）：
        //   label 0        = blank（不输出字符）
        //   label 1..N     = 字典文件第 1..N 行（每行一个字符）
        //   label N+1(末位) = 空格（use_space_char=True 追加在末尾）
        // rec.onnx 输出类别数 C = N + 2（v6 medium: 18708 + 2 = 18710）。
        // 历史 Bug：旧实现把空格插在首位且假设 blank 在末尾，导致所有字符
        // 按字典整体偏移一位被解码成相邻字符（识别结果全是乱码/错字）。
        var list = new List<string>(rawLines.Length + 2) { string.Empty }; // index 0: blank 占位
        foreach (var rawLine in rawLines)
        {
            // 字典行即字符本身；仅剥离 BOM 与行尾符，内容中的空白（如全角空格）必须原样保留
            var line = rawLine.TrimEnd('\r', '\n');
            if (line.Length > 0 && line[0] == '﻿')
            {
                line = line.Substring(1);
            }
            // 兼容 "index\tchar" 格式的字典文件（若有）；空行保留为空条目以维持标签对齐
            var tab = line.IndexOf('\t');
            var ch = tab >= 0 ? line.Substring(tab + 1) : line;
            list.Add(ch);
        }
        list.Add(" "); // 末位：空格类
        return list.ToArray();
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!_modelReady || _initTask == null) return;

        try
        {
            var holder = await _initTask.Value.WaitAsync(ct).ConfigureAwait(false);
            if (holder.DetSession == null)
                throw OcrException.ModelLoadFailed("Det session not initialized");
        }
        catch (OperationCanceledException oce)
        {
            throw OcrException.Cancelled(oce.Message, oce);
        }
        catch (OcrException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw OcrException.ModelLoadFailed(ex.Message, ex);
        }
    }

    public async Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default)
    {
        try
        {
            if (!_modelReady)
            {
                throw OcrException.ModelLoadFailed(
                    "PP-OCRv6 models missing. Please place det.onnx/cls.onnx/rec.onnx/ppocr_keys.txt under assets/models or %APPDATA%/QuickTranslate/models");
            }

            var sw = Stopwatch.StartNew();

            InferenceSessionsHolder? holder = null;
            if (_initTask != null)
            {
                try
                {
                    holder = await _initTask.Value.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    throw OcrException.Cancelled(oce.Message, oce);
                }
            }

            if (holder == null || holder.DetSession == null || holder.RecSession == null)
            {
                throw OcrException.ModelLoadFailed("ONNX sessions (det/rec) not initialized");
            }

            ct.ThrowIfCancellationRequested();

            // ===== PREPROCESS =====
            var preprocessStart = sw.Elapsed;
            var (detInput, detScaleW, detScaleH, detInputW, detInputH) = PreprocessDet(frame.Bitmap);
            var preprocess = sw.Elapsed - preprocessStart;

            ct.ThrowIfCancellationRequested();

            // ===== DETECTOR =====
            var detectorStart = sw.Elapsed;
            var detBoxes = RunDetector(holder.DetSession, detInput, detInputW, detInputH, detScaleW, detScaleH,
                frame.Region.Width, frame.Region.Height);
            var detectorElapsed = sw.Elapsed - detectorStart;

            // 诊断日志：检测框原始尺寸（定位框太扁/偏离问题）
            _logger.LogDebug("DetBoxes: count={Count} frameRegion=({RX},{RY},{RW}x{RH})",
                detBoxes.Count, frame.Region.X, frame.Region.Y, frame.Region.Width, frame.Region.Height);
            foreach (var db in detBoxes)
            {
                _logger.LogDebug("  DetBox: ({X},{Y},{W}x{H})", db.X, db.Y, db.Width, db.Height);
            }

            ct.ThrowIfCancellationRequested();

            // ===== CLASSIFIER + RECOGNIZER (per box) =====
            var lines = new List<OcrLine>();
            var classifierTotal = TimeSpan.Zero;
            var recognizerTotal = TimeSpan.Zero;

            for (int lineIdx = 0; lineIdx < detBoxes.Count; lineIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var box = detBoxes[lineIdx];

                // detBoxes 是截图位图内的 0-based 局部坐标（DbPostprocess 按位图尺寸计算）。
                // 而 WordSelector 用屏幕绝对坐标的鼠标比对，若直接返回局部坐标将永远 miss →
                // “未检测到可翻译的单词 / 单词识别不可用”。因此：
                //   - CropBitmap 用局部 box（它相对 frame.Bitmap 裁剪）；
                //   - OcrLine/OcrWord 用平移到屏幕绝对坐标的 screenBox（frame.Region 是屏幕坐标）。
                var screenBox = new PhysicalRect(
                    box.X + frame.Region.X,
                    box.Y + frame.Region.Y,
                    box.Width,
                    box.Height);

                // Crop line image from original bitmap
                using var lineBmp = CropBitmap(frame.Bitmap, box);
                if (lineBmp == null) continue;

                // ===== CLASSIFIER (optional) =====
                var clsStart = sw.Elapsed;
                float clsAngle = 0f;
                bool clsNeedRotate = false;
                if (holder.ClsSession != null)
                {
                    var clsInput = PreprocessCls(lineBmp);
                    (clsAngle, clsNeedRotate) = RunClassifier(holder.ClsSession, clsInput);
                }
                classifierTotal += sw.Elapsed - clsStart;

                Bitmap? orientedBmp = null;
                try
                {
                    if (clsNeedRotate) orientedBmp = Rotate180(lineBmp);
                    var recSource = orientedBmp ?? lineBmp;

                    // ===== RECOGNIZER =====
                    var recStart = sw.Elapsed;
                    var recInput = PreprocessRec(recSource);
                    var recText = RunRecognizer(holder.RecSession, recInput, holder.CharDictionary);
                    recognizerTotal += sw.Elapsed - recStart;

                    if (!string.IsNullOrWhiteSpace(recText))
                    {
                        // 词框解析策略（spec 8.3）：优先垂直投影精确词框（空白间隔/垂直投影），
                        // 段数与 token 数不一致（CJK 混排、噪声等）时回退加权比例法（字符区间估计）。
                        IReadOnlyList<OcrWord> words;
                        if (ProjectionWordSegmenter.TrySegment(
                                recSource, recText, box, frame.Region, clsNeedRotate, lineIdx, out var projWords))
                        {
                            words = projWords;
                            _logger.LogDebug("WordBox: strategy=projection words={Count} line={Idx}", projWords.Count, lineIdx);
                        }
                        else
                        {
                            words = BuildWords(recText, screenBox, lineIdx);
                            _logger.LogDebug("WordBox: strategy=proportional words={Count} line={Idx}", words.Count, lineIdx);
                        }
                        var ocrLine = new OcrLine(screenBox, words, recText, clsAngle);
                        lines.Add(ocrLine);
                    }
                }
                finally
                {
                    orientedBmp?.Dispose();
                }
            }

            // ===== POSTPROCESS =====
            var postprocessStart = sw.Elapsed;
            // Already done above per box; just finalize timing
            var postprocessElapsed = sw.Elapsed - postprocessStart;

            sw.Stop();

            var timings = new OcrTimings(
                preprocess,
                detectorElapsed,
                classifierTotal,
                recognizerTotal,
                postprocessElapsed);

            var result = new OcrLayoutResult(
                frame.Region,
                lines,
                timings,
                DateTimeOffset.Now,
                DpiX: 96,
                DpiY: 96,
                EngineName: EngineName,
                FromCache: false);

            _logger.LogInformation(
                "OCR finished: Engine={EngineName} Lines={LineCount} PreprocessMs={PreprocessMs} DetectorMs={DetectorMs} ClassifierMs={ClassifierMs} RecognizerMs={RecognizerMs} PostprocessMs={PostprocessMs}",
                EngineName,
                result.LineCount,
                timings.Preprocess.TotalMilliseconds,
                timings.Detector.TotalMilliseconds,
                timings.Classifier.TotalMilliseconds,
                timings.Recognizer.TotalMilliseconds,
                timings.Postprocess.TotalMilliseconds);

            return result;
        }
        catch (OperationCanceledException oce)
        {
            throw OcrException.Cancelled(oce.Message, oce);
        }
        catch (OcrException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw OcrException.InferenceFailed(ex.Message, ex);
        }
    }

    // ==================== DETECTOR ====================

    private static readonly float[] DetMean = new[] { 0.485f, 0.456f, 0.406f };
    private static readonly float[] DetStd = new[] { 0.229f, 0.224f, 0.225f };
    private const int DetMaxSideLen = 960;

    private static (float[] Input, float ScaleW, float ScaleH, int InputW, int InputH) PreprocessDet(Bitmap src)
    {
        int srcW = src.Width, srcH = src.Height;

        // Resize so long side <= DetMaxSideLen, keeping aspect ratio
        float ratio = Math.Min((float)DetMaxSideLen / srcW, (float)DetMaxSideLen / srcH);
        int resizeW = Math.Max(1, (int)Math.Round(srcW * ratio));
        int resizeH = Math.Max(1, (int)Math.Round(srcH * ratio));

        // PP-OCR det expects multiples of 32
        int inputW = (resizeW + 31) / 32 * 32;
        int inputH = (resizeH + 31) / 32 * 32;

        float scaleW = (float)resizeW / srcW;
        float scaleH = (float)resizeH / srcH;

        using var resized = new Bitmap(inputW, inputH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.Clear(Color.Black);
            g.DrawImage(src, new Rectangle(0, 0, resizeW, resizeH), 0, 0, srcW, srcH, GraphicsUnit.Pixel);
        }

        var bytes = new byte[inputW * inputH * 4];
        var bmpData = resized.LockBits(new Rectangle(0, 0, inputW, inputH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bmpData.Scan0, bytes, 0, bytes.Length);
        }
        finally
        {
            resized.UnlockBits(bmpData);
        }

        // HWC (BGRA32) -> CHW，通道顺序 BGR（与 PaddleOCR DecodeImage img_mode=BGR 一致）。
        // 注意：PaddleOCR 对 BGR 图像直接按 [0.485, 0.456, 0.406] 逐通道归一化，
        // 即 B 通道用 0.485、R 通道用 0.406（历史怪癖，勿“修正”成 RGB 顺序，
        // 否则与模型训练分布不符，检测框召回率明显下降）。
        var chw = new float[3 * inputH * inputW];
        int hw = inputH * inputW;
        for (int y = 0; y < inputH; y++)
        {
            for (int x = 0; x < inputW; x++)
            {
                int idx = (y * inputW + x) * 4;
                byte b = bytes[idx], g = bytes[idx + 1], r = bytes[idx + 2];
                int chwIdx = y * inputW + x;
                chw[chwIdx] = ((b / 255f) - DetMean[0]) / DetStd[0];           // B
                chw[hw + chwIdx] = ((g / 255f) - DetMean[1]) / DetStd[1];      // G
                chw[2 * hw + chwIdx] = ((r / 255f) - DetMean[2]) / DetStd[2];  // R
            }
        }

        return (chw, scaleW, scaleH, inputW, inputH);
    }

    private static List<PhysicalRect> RunDetector(
        InferenceSession session, float[] input,
        int inputW, int inputH,
        float scaleW, float scaleH,
        int origW, int origH)
    {
        var inputMeta = session.InputMetadata;
        var inputName = inputMeta.Keys.First();
        var dims = new[] { 1, 3, inputH, inputW };

        var tensor = new DenseTensor<float>(input, dims);
        var inputValues = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var outputs = session.Run(new[] { inputValues });

        var output = outputs.First();
        var outTensor = output.AsTensor<float>();
        // shape [1, 1, H, W] or [1, H, W] or [H, W]
        var predMap = outTensor.ToArray();
        int outH, outW;

        var shape = outTensor.Dimensions.ToArray();
        if (shape.Length == 4) { outH = shape[2]; outW = shape[3]; }
        else if (shape.Length == 3) { outH = shape[1]; outW = shape[2]; }
        else if (shape.Length == 2) { outH = shape[0]; outW = shape[1]; }
        else { predMap = new float[inputH / 4 * inputW / 4]; outH = inputH / 4; outW = inputW / 4; Array.Fill(predMap, 0f); }

        return DbPostprocess(predMap, outW, outH, inputW, inputH, scaleW, scaleH, origW, origH);
    }

    private static List<PhysicalRect> DbPostprocess(
        float[] predMap, int outW, int outH,
        int inputW, int inputH,
        float scaleW, float scaleH,
        int origW, int origH)
    {
        // Simple DB threshold + box extraction
        // 阈值与 PP-OCRv6_medium_det inference.yml 的 DBPostProcess 一致：
        // thresh=0.2, box_thresh=0.45, unclip_ratio=1.4
        const float thresh = 0.2f;
        const float boxThresh = 0.45f;
        const int minSize = 3;

        // 1. Threshold: create binary mask
        var mask = new byte[outH * outW];
        for (int i = 0; i < predMap.Length && i < mask.Length; i++)
        {
            mask[i] = predMap[i] > thresh ? (byte)1 : (byte)0;
        }

        // 2. Simple connected components via flood-fill (bounding boxes per component)
        var visited = new bool[outH * outW];
        var boxes = new List<PhysicalRect>();

        // Downscale factor from det input to pred map
        float predScaleX = (float)inputW / outW;
        float predScaleY = (float)inputH / outH;

        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                int idx = y * outW + x;
                if (mask[idx] == 0 || visited[idx]) continue;

                // BFS flood fill
                var q = new Queue<(int X, int Y)>();
                q.Enqueue((x, y));
                visited[idx] = true;
                int minX = x, maxX = x, minY = y, maxY = y;
                float sumVal = 0;
                int count = 0;

                while (q.Count > 0)
                {
                    var (cx, cy) = q.Dequeue();
                    var ci = cy * outW + cx;
                    if (ci < predMap.Length) sumVal += predMap[ci];
                    count++;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    // 4 neighbors
                    if (cx > 0)
                    {
                        var ni = cy * outW + cx - 1;
                        if (mask[ni] == 1 && !visited[ni]) { visited[ni] = true; q.Enqueue((cx - 1, cy)); }
                    }
                    if (cx < outW - 1)
                    {
                        var ni = cy * outW + cx + 1;
                        if (mask[ni] == 1 && !visited[ni]) { visited[ni] = true; q.Enqueue((cx + 1, cy)); }
                    }
                    if (cy > 0)
                    {
                        var ni = (cy - 1) * outW + cx;
                        if (mask[ni] == 1 && !visited[ni]) { visited[ni] = true; q.Enqueue((cx, cy - 1)); }
                    }
                    if (cy < outH - 1)
                    {
                        var ni = (cy + 1) * outW + cx;
                        if (mask[ni] == 1 && !visited[ni]) { visited[ni] = true; q.Enqueue((cx, cy + 1)); }
                    }
                }

                if (count == 0) continue;
                if ((maxX - minX + 1) < minSize || (maxY - minY + 1) < minSize) continue;
                if (sumVal / count < boxThresh) continue;

                // Map back to original image coordinates:
                // pred coords -> det input coords (via predScale) -> scaled resize coords (via 1/scale)
                int rx1 = (int)Math.Round(minX * predScaleX / scaleW);
                int ry1 = (int)Math.Round(minY * predScaleY / scaleH);
                int rx2 = (int)Math.Round((maxX + 1) * predScaleX / scaleW);
                int ry2 = (int)Math.Round((maxY + 1) * predScaleY / scaleH);

                // Unclip: PP-OCR standard expands box around its centroid.
                // DB threshold yields a tight pixel-stroke bounding box (e.g. 9-13px height
                // for 24px text). Standard formula (per PaddleOCR ppocr/postprocess/db_postprocess.py):
                //   perimeter = 2*(w+h), area = w*h, dist = area * unclip_ratio / perimeter
                // unclip_ratio=1.4 与 inference.yml DBPostProcess 一致。
                int w0 = rx2 - rx1, h0 = ry2 - ry1;
                if (w0 <= 0 || h0 <= 0) continue;
                float perimeter = 2f * (w0 + h0);
                float area = (float)w0 * h0;
                float unclipRatio = 1.4f;
                float dist = (area * unclipRatio) / Math.Max(perimeter, 1f);
                int dx = (int)Math.Round(dist);
                int dy = (int)Math.Round(dist);
                // Minimum vertical expansion: ensure we cover at least a typical glyph margin.
                // For very thin boxes (h=6-12px) the dist formula still gives a small value,
                // so clamp dy to at least half the original height to guarantee expansion.
                int minDy = (int)Math.Round(h0 * 0.75);
                dy = Math.Max(dy, minDy);
                rx1 = Math.Max(0, rx1 - dx);
                ry1 = Math.Max(0, ry1 - dy);
                rx2 = Math.Min(origW, rx2 + dx);
                ry2 = Math.Min(origH, ry2 + dy);

                int w = rx2 - rx1, h = ry2 - ry1;
                if (w <= 0 || h <= 0) continue;
                boxes.Add(new PhysicalRect(rx1, ry1, w, h));
            }
        }

        // Merge vertically-overlapping boxes that belong to the same text line.
        // Flood-fill tends to fragment a single line into multiple thin slivers;
        // merge any two boxes whose vertical ranges overlap significantly.
        boxes.Sort((a, b) =>
        {
            int dx = a.X.CompareTo(b.X);
            if (dx != 0) return dx;
            return a.Y.CompareTo(b.Y);
        });

        var merged = new List<PhysicalRect>();
        foreach (var b in boxes)
        {
            bool absorbed = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var m = merged[i];
                // Vertical overlap ratio
                int vInt = Math.Min(b.Bottom, m.Bottom) - Math.Max(b.Y, m.Y);
                int vMin = Math.Min(b.Height, m.Height);
                if (vInt > vMin * 0.3)
                {
                    // Same line → union
                    int nx1 = Math.Min(m.X, b.X);
                    int ny1 = Math.Min(m.Y, b.Y);
                    int nx2 = Math.Max(m.Right, b.Right);
                    int ny2 = Math.Max(m.Bottom, b.Bottom);
                    merged[i] = new PhysicalRect(nx1, ny1, nx2 - nx1, ny2 - ny1);
                    absorbed = true;
                    break;
                }
            }
            if (!absorbed) merged.Add(b);
        }
        boxes = merged;

        // Final vertical normalize: DB flood-fill components are tight stroke boxes.
        // After merge each line's box is still vertically under-sized (e.g. 17px for 30px text).
        // Normalize each box's height to roughly 0.055x the frame's height (≈30px for 540p
        // capture), which matches common body text on screen, but cap to a sensible range.
        const double targetRatio = 0.06; // 6% of frame height ≈ typical line gap
        double baselineLineH = origH * targetRatio;
        int minH = Math.Max(18, (int)Math.Round(baselineLineH * 0.6));
        int maxH = Math.Max(60, (int)Math.Round(baselineLineH * 2.0));

        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (b.Height >= minH && b.Height <= maxH) continue;
            int h = Math.Clamp(b.Height, minH, maxH);
            int cy = b.Y + b.Height / 2;
            int ny1 = cy - h / 2;
            int ny2 = ny1 + h;
            ny1 = Math.Max(0, ny1);
            ny2 = Math.Min(origH, ny2);
            boxes[i] = new PhysicalRect(b.X, ny1, b.Width, ny2 - ny1);
        }

        // Sort by Y, then X (reading order)
        boxes.Sort((a, b) =>
        {
            int dy = a.Y.CompareTo(b.Y);
            if (Math.Abs(dy) > Math.Max(a.Height, b.Height) / 2) return dy;
            return a.X.CompareTo(b.X);
        });

        return boxes;
    }

    // ==================== CLASSIFIER ====================

    private static readonly float[] ClsMean = new[] { 0.5f, 0.5f, 0.5f };
    private static readonly float[] ClsStd = new[] { 0.5f, 0.5f, 0.5f };
    private const int ClsW = 48;
    private const int ClsH = 48;

    private static float[] PreprocessCls(Bitmap src)
    {
        using var resized = new Bitmap(ClsW, ClsH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.Clear(Color.Black);
            var ratio = Math.Min((float)ClsW / src.Width, (float)ClsH / src.Height);
            int dw = (int)Math.Round(src.Width * ratio);
            int dh = (int)Math.Round(src.Height * ratio);
            int dx = (ClsW - dw) / 2;
            int dy = (ClsH - dh) / 2;
            g.DrawImage(src, new Rectangle(dx, dy, dw, dh), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }

        var bytes = new byte[ClsW * ClsH * 4];
        var bmpData = resized.LockBits(new Rectangle(0, 0, ClsW, ClsH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(bmpData.Scan0, bytes, 0, bytes.Length); }
        finally { resized.UnlockBits(bmpData); }

        var chw = new float[3 * ClsH * ClsW];
        int hw = ClsH * ClsW;
        for (int i = 0; i < hw; i++)
        {
            int bi = i * 4;
            byte b = bytes[bi], g = bytes[bi + 1], r = bytes[bi + 2];
            chw[i] = ((r / 255f) - ClsMean[0]) / ClsStd[0];
            chw[hw + i] = ((g / 255f) - ClsMean[1]) / ClsStd[1];
            chw[2 * hw + i] = ((b / 255f) - ClsMean[2]) / ClsStd[2];
        }
        return chw;
    }

    private static (float Angle, bool NeedRotate) RunClassifier(InferenceSession session, float[] input)
    {
        var inputName = session.InputMetadata.Keys.First();
        var dims = new[] { 1, 3, ClsH, ClsW };
        var tensor = new DenseTensor<float>(input, dims);
        var inputValues = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var outputs = session.Run(new[] { inputValues });
        var arr = outputs.First().AsTensor<float>().ToArray();

        // Output shape [1, 2]; label 0 = 0 deg, label 1 = 180 deg
        if (arr.Length < 2) return (0f, false);
        // Softmax threshold
        float sum = (float)(Math.Exp(arr[0]) + Math.Exp(arr[1]));
        float p0 = (float)Math.Exp(arr[0]) / sum;
        float p1 = (float)Math.Exp(arr[1]) / sum;
        const float rotateThresh = 0.5f;
        bool need = p1 > rotateThresh && p1 > p0;
        return (need ? 180f : 0f, need);
    }

    // ==================== RECOGNIZER ====================

    private static readonly float[] RecMean = new[] { 0.5f, 0.5f, 0.5f };
    private static readonly float[] RecStd = new[] { 0.5f, 0.5f, 0.5f };
    private const int RecH = 48;
    private const int RecMinW = 48;
    // 模型导出为动态宽度（TRT 配置最大 3200）。官方静态推理用 320 + 补黑，
    // 但超宽行压缩到 320 会失真；这里放宽到 1280 保证长句纵横比不失真。
    private const int RecMaxW = 1280;

    private static (float[] Input, int Width) PreprocessRec(Bitmap src)
    {
        // 高度固定 RecH，宽度按纵横比缩放（与 PaddleOCR RecResizeImg 一致），
        // 右侧不足部分保留黑色背景。历史 Bug：宽行被等比“压缩”到 320 宽，
        // 纵横比失真导致长句识别错误率暴涨——这里绝不能拉伸。
        // 官方训练 image_shape 为 3x48x320，超宽行 clamp 到 RecMaxW 并右侧补黑
        // （CTC 对补黑时间步输出 blank，无副作用）。
        float ratio = (float)RecH / src.Height;
        int naturalW = (int)Math.Round(src.Width * ratio);
        int targetW = Math.Clamp(naturalW, RecMinW, RecMaxW);
        targetW = (targetW + 3) / 4 * 4;

        using var resized = new Bitmap(targetW, RecH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.Clear(Color.Black);
            // 按自然比例绘制（不拉伸）；若 naturalW 超过 targetW（clamp 场景）则等比缩到 targetW
            int drawW = Math.Min(naturalW, targetW);
            g.DrawImage(src, new Rectangle(0, 0, drawW, RecH), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }

        var bytes = new byte[targetW * RecH * 4];
        var bmpData = resized.LockBits(new Rectangle(0, 0, targetW, RecH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(bmpData.Scan0, bytes, 0, bytes.Length); }
        finally { resized.UnlockBits(bmpData); }

        int hw = RecH * targetW;
        var chw = new float[3 * hw];
        for (int i = 0; i < hw; i++)
        {
            int bi = i * 4;
            byte b = bytes[bi], g = bytes[bi + 1], r = bytes[bi + 2];
            chw[i] = ((r / 255f) - RecMean[0]) / RecStd[0];
            chw[hw + i] = ((g / 255f) - RecMean[1]) / RecStd[1];
            chw[2 * hw + i] = ((b / 255f) - RecMean[2]) / RecStd[2];
        }
        return (chw, targetW);
    }

    private static string RunRecognizer(InferenceSession session, (float[] Input, int Width) input, string[] dict)
    {
        var (data, w) = input;
        var inputName = session.InputMetadata.Keys.First();
        var dims = new[] { 1, 3, RecH, w };
        var tensor = new DenseTensor<float>(data, dims);
        var inputValues = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var outputs = session.Run(new[] { inputValues });

        var outTensor = outputs.First().AsTensor<float>();
        var outShape = outTensor.Dimensions.ToArray();
        // Expected [1, T, C] where C = num_classes + 1 (blank at end)
        int T, C;
        if (outShape.Length == 3) { T = outShape[1]; C = outShape[2]; }
        else if (outShape.Length == 2) { T = outShape[0]; C = outShape[1]; }
        else return string.Empty;

        var probs = outTensor.ToArray();

        return CtcGreedyDecode(probs, T, C, dict);
    }

    private static string CtcGreedyDecode(float[] probs, int T, int C, string[] dict)
    {
        // PaddleOCR CTC 布局：blank 固定在 index 0，字典字符从 1 开始，空格在末位 C-1。
        // dict 数组由 BuildCharDictionary 按同一布局构建（dict[i] 即 label i 的字符）。
        int blankIdx = 0;
        var sb = new System.Text.StringBuilder();
        int prevIdx = -1;

        for (int t = 0; t < T; t++)
        {
            int start = t * C;
            int bestIdx = 0;
            float bestVal = float.MinValue;
            for (int c = 0; c < C; c++)
            {
                float v = probs[start + c];
                if (v > bestVal) { bestVal = v; bestIdx = c; }
            }

            if (bestIdx != blankIdx && bestIdx != prevIdx)
            {
                if (bestIdx >= 0 && bestIdx < dict.Length)
                {
                    sb.Append(dict[bestIdx]);
                }
            }
            prevIdx = bestIdx;
        }

        return sb.ToString().Trim();
    }

    // ==================== UTILS ====================

    private static Bitmap? CropBitmap(Bitmap src, PhysicalRect box)
    {
        if (box.IsEmpty || box.Right > src.Width || box.Bottom > src.Height)
        {
            int x = Math.Clamp(box.X, 0, src.Width - 1);
            int y = Math.Clamp(box.Y, 0, src.Height - 1);
            int w = Math.Clamp(box.Width, 1, src.Width - x);
            int h = Math.Clamp(box.Height, 1, src.Height - y);
            box = new PhysicalRect(x, y, w, h);
        }
        if (box.Width <= 0 || box.Height <= 0) return null;

        try
        {
            var rect = new Rectangle(box.X, box.Y, box.Width, box.Height);
            return src.Clone(rect, PixelFormat.Format32bppArgb);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? Rotate180(Bitmap src)
    {
        try
        {
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.TranslateTransform(src.Width / 2f, src.Height / 2f);
            g.RotateTransform(180f);
            g.TranslateTransform(-src.Width / 2f, -src.Height / 2f);
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<OcrWord> BuildWords(string text, PhysicalRect lineBox, int lineIdx)
    {
        var words = new List<OcrWord>();
        if (string.IsNullOrEmpty(text)) return words;

        bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF ||
                              c >= 0x3040 && c <= 0x30FF || c >= 0xAC00 && c <= 0xD7AF;
        bool IsWide(char c) => IsCjk(c) || c >= 0xFF01 && c <= 0xFF60 || c >= 0xFFE0 && c <= 0xFFE6; // 全角标点

        // 计算比例字符宽度：CJK/全角占 2 份，其他（拉丁、数字、窄标点）占 1 份。
        // 这样"English中文123"不会被简单 1/N 平分导致每个字符宽度被拉大/压缩。
        int totalUnits = 0;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch)) totalUnits += 1;
            else if (IsWide(ch)) totalUnits += 2;
            else totalUnits += 1;
        }
        if (totalUnits <= 0) totalUnits = text.Length;
        int unitW = Math.Max(1, lineBox.Width / totalUnits);
        // 剩余像素让后面的字符吃掉（避免因整除截断在末端留下空隙）
        int remainder = lineBox.Width - unitW * totalUnits;

        int cursorX = lineBox.X;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                int w = unitW;
                if (remainder > 0) { w++; remainder--; }
                cursorX += w;
                i++;
                continue;
            }

            if (IsCjk(c))
            {
                int w = unitW * 2;
                if (remainder > 0) { w++; remainder--; }
                int x1 = cursorX;
                int x2 = Math.Min(lineBox.Right, x1 + w);
                var wbox = new PhysicalRect(x1, lineBox.Y, Math.Max(1, x2 - x1), lineBox.Height);
                words.Add(new OcrWord(wbox, c.ToString(), 0.85f, lineIdx));
                cursorX = x2;
                i++;
            }
            else
            {
                // Group a run of non-space non-CJK as one word (Latin/numbers/punctuation sequence)
                int start = i;
                int wordUnits = 0;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && !IsCjk(text[i]))
                {
                    wordUnits += IsWide(text[i]) ? 2 : 1;
                    i++;
                }
                int len = i - start;
                if (len <= 0) continue;

                int wordW = unitW * wordUnits;
                if (remainder > 0) { wordW += Math.Min(remainder, wordUnits); remainder -= Math.Min(remainder, wordUnits); }
                int x1 = cursorX;
                int x2 = Math.Min(lineBox.Right, x1 + wordW);
                int wordBoxW = Math.Max(1, x2 - x1);
                // 英文单词：在框内再收缩 5% 左右的左右边距，避免贴到邻词/框边缘
                int pad = (int)Math.Round(wordBoxW * 0.03);
                int nx1 = x1 + pad;
                int nx2 = x2 - pad;
                if (nx2 - nx1 < 2) { nx1 = x1; nx2 = x2; }
                var wbox = new PhysicalRect(nx1, lineBox.Y, Math.Max(1, nx2 - nx1), lineBox.Height);
                words.Add(new OcrWord(wbox, text.Substring(start, len), 0.9f, lineIdx));
                cursorX = x2;
            }
        }
        return words;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initTask != null && _initTask.IsValueCreated && _initTask.Value.IsCompletedSuccessfully)
        {
            var holder = _initTask.Value.Result;
            holder.DetSession?.Dispose();
            holder.ClsSession?.Dispose();
            holder.RecSession?.Dispose();
        }
    }

    private class InferenceSessionsHolder
    {
        public InferenceSession? DetSession;
        public InferenceSession? ClsSession;
        public InferenceSession? RecSession;
        public string[] CharDictionary = Array.Empty<string>();
    }
}
