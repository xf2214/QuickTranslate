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
            @"E:\翻译\assets\models"
        };

        foreach (var dir in candidateDirs)
        {
            if (CheckModelFiles(dir))
            {
                _modelsDirectory = dir;
                _modelReady = true;
                _logger.LogInformation("PP-OCRv6 models found in {ModelDir}", dir);
                break;
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
        // ppocr_keys.txt: first line is blank space index, then each char on its own line
        // Standard format: line N => character for label N
        var list = new List<string>(rawLines.Length + 1) { " " }; // index 0 is space (CTC blank is last)
        foreach (var line in rawLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                list.Add(" ");
            }
            else
            {
                // Some key files have tab-separated index\tchar
                var tab = line.IndexOf('\t');
                var ch = tab >= 0 ? line.Substring(tab + 1) : line;
                list.Add(ch.Length == 0 ? " " : ch);
            }
        }
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

            ct.ThrowIfCancellationRequested();

            // ===== CLASSIFIER + RECOGNIZER (per box) =====
            var lines = new List<OcrLine>();
            var classifierTotal = TimeSpan.Zero;
            var recognizerTotal = TimeSpan.Zero;

            for (int lineIdx = 0; lineIdx < detBoxes.Count; lineIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var box = detBoxes[lineIdx];

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
                        // Build words from text (simple split by whitespace for Latin; single chars for CJK)
                        var words = BuildWords(recText, box, lineIdx);
                        var ocrLine = new OcrLine(box, words, recText, clsAngle);
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

        // HWC (BGRA32) -> CHW (RGB normalized)
        var chw = new float[3 * inputH * inputW];
        int hw = inputH * inputW;
        for (int y = 0; y < inputH; y++)
        {
            for (int x = 0; x < inputW; x++)
            {
                int idx = (y * inputW + x) * 4;
                byte b = bytes[idx], g = bytes[idx + 1], r = bytes[idx + 2];
                int chwIdx = y * inputW + x;
                chw[chwIdx] = ((r / 255f) - DetMean[0]) / DetStd[0];           // R
                chw[hw + chwIdx] = ((g / 255f) - DetMean[1]) / DetStd[1];      // G
                chw[2 * hw + chwIdx] = ((b / 255f) - DetMean[2]) / DetStd[2];  // B
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
        const float thresh = 0.3f;
        const float boxThresh = 0.5f;
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

                // Expand slightly
                int expand = 2;
                rx1 = Math.Max(0, rx1 - expand);
                ry1 = Math.Max(0, ry1 - expand);
                rx2 = Math.Min(origW, rx2 + expand);
                ry2 = Math.Min(origH, ry2 + expand);

                int w = rx2 - rx1, h = ry2 - ry1;
                if (w <= 0 || h <= 0) continue;
                boxes.Add(new PhysicalRect(rx1, ry1, w, h));
            }
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
    private const int RecMaxW = 320;

    private static (float[] Input, int Width) PreprocessRec(Bitmap src)
    {
        // Height fixed to RecH; width scaled proportionally, clamped + multiple of 4
        float ratio = (float)RecH / src.Height;
        int targetW = (int)Math.Round(src.Width * ratio);
        targetW = Math.Max(RecMinW, Math.Min(RecMaxW, targetW));
        targetW = (targetW + 3) / 4 * 4;

        using var resized = new Bitmap(targetW, RecH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.Clear(Color.Black);
            g.DrawImage(src, new Rectangle(0, 0, targetW, RecH), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
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
        // blank class index = C - 1
        int blankIdx = C - 1;
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
                // Map bestIdx to dict. dict index 0 is space (label 1 in raw? careful).
                // Our BuildCharDictionary inserts " " at index 0, then each raw line => index 1..N.
                // Blank is at C-1 (usually blank=dict.Count, which matches "blank" class for ppocr rec).
                int dictIdx = bestIdx;
                if (dictIdx >= 0 && dictIdx < dict.Length)
                {
                    sb.Append(dict[dictIdx]);
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

        // Split: each CJK char is a word; ASCII sequences grouped by whitespace
        int charWidth = Math.Max(1, lineBox.Width / text.Length);
        int cursorX = lineBox.X;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; cursorX += Math.Max(1, charWidth / 2); continue; }

            if (IsCjk(c))
            {
                var wbox = new PhysicalRect(cursorX, lineBox.Y, charWidth, lineBox.Height);
                words.Add(new OcrWord(wbox, c.ToString(), 0.85f, lineIdx));
                cursorX += charWidth;
                i++;
            }
            else
            {
                // Group a run of non-space non-CJK as one word
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && !IsCjk(text[i])) i++;
                int len = i - start;
                if (len <= 0) continue;
                int wordW = charWidth * len;
                int x1 = cursorX;
                int x2 = cursorX + wordW;
                if (x2 > lineBox.Right) { x2 = lineBox.Right; wordW = x2 - x1; }
                var wbox = new PhysicalRect(x1, lineBox.Y, Math.Max(1, wordW), lineBox.Height);
                words.Add(new OcrWord(wbox, text.Substring(start, len), 0.9f, lineIdx));
                cursorX = x2 + Math.Max(1, charWidth / 3);
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
