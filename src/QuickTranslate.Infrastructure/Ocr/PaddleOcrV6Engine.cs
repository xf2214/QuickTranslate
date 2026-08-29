using System.Buffers;
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

    // 同帧 det 结果缓存：触带扩展会对同一截图帧再次调用识别（更宽的焦点带），
    // det 结果与焦点带无关 → 复用可省一次 det（~390ms）。
    // 缓存以（位图引用, 裁剪区）为键且支持包含式命中：新裁剪区落在已缓存裁剪区内
    // 时直接复用（盒子存帧局部坐标），带加宽重试无需重跑 det。
    private readonly object _detCacheLock = new();
    private Bitmap? _detCacheBitmap;
    private PhysicalRect _detCacheCrop;
    private List<PhysicalRect>? _detCacheBoxes;
    private DateTime _detCacheAtUtc;
    private const double DetCacheTtlSeconds = 8;

    // 同帧 rec 行级缓存：触带扩展/重试对同一帧再次识别时，已识别过的行
    // 直接复用（文本+词框+置信度），跳过 cls/rec/词框切分，只跑新进行的行。
    // 同帧命中条件：位图引用 + Region（词框是屏幕绝对坐标，Region 不同不可复用）。
    // det 在裁剪输入 vs 全帧输入下的盒子可能有数 px 抖动（实测高度差可达 5px，
    // unclip 扩展随盒高变化），因此行匹配用 IoU ≥ 0.6 而非精确相等：
    // 同一行 IoU 典型 >0.85，相邻行垂直分离 IoU ≈ 0，既不漏命中也不误认邻行。
    // 条目数上限就是单帧行数（个位数～十几），线性扫描开销可忽略。
    private readonly object _recCacheLock = new();
    private Bitmap? _recCacheBitmap;
    private PhysicalRect _recCacheRegion;
    private DateTime _recCacheAtUtc;
    private List<RecLineCacheEntry>? _recCache;
    // 最近一次 RecognizeAsync 的行级缓存命中数（供回归测试断言，非线程安全仅作诊断）
    internal int LastRecCacheHits;
    private const double RecCacheMinIou = 0.6;

    private sealed record RecLineCacheEntry(
        PhysicalRect Box,
        string? Text,
        IReadOnlyList<OcrWord> Words,
        string Strategy,
        float Angle,
        float Confidence,
        PhysicalRect RecBox);

    private static double BoxIou(PhysicalRect a, PhysicalRect b)
    {
        int ix1 = Math.Max(a.X, b.X);
        int iy1 = Math.Max(a.Y, b.Y);
        int ix2 = Math.Min(a.Right, b.Right);
        int iy2 = Math.Min(a.Bottom, b.Bottom);
        int iw = ix2 - ix1, ih = iy2 - iy1;
        if (iw <= 0 || ih <= 0) return 0;
        long inter = (long)iw * ih;
        long union = (long)a.Width * a.Height + (long)b.Width * b.Height - inter;
        return union <= 0 ? 0 : (double)inter / union;
    }

    // 焦点带内最多识别的行数：rec ~75ms/行是主要耗时，块生长实际只会用到
    // 锚点附近的行，远离光标的行识别了也不会被选中，按到带中心距离取最近 N 行。
    private const int MaxLinesToRecognize = 8;

    // 主墨水带裁剪的上下 padding（像素）：保留少量字形上下伸部余量，
    // 避免贴边裁切抗锯齿过渡带。
    private const int RecBandPaddingPx = 2;

    public string EngineName => "PP-OCRv6-ONNX";
    public bool IsAvailable => _modelReady;

    public event EventHandler? SessionCreated;

    /// <summary>词合理性检查器（通常由 ECDICT 词典背书）：粘连词拆分（unfuse）前裁决
    /// token/片段是否为合理词，防止正常单词被字距缝隙拦腰切断（如 commit→com|mit）。
    /// null = 不做词汇佐证（保持纯几何判定）。</summary>
    private readonly Func<string, bool>? _isPlausibleWord;

    public PaddleOcrV6Engine(
        IOptions<AppSettings> settings,
        IAppDataProvider appDataProvider,
        ILogger<PaddleOcrV6Engine> logger,
        Func<string, bool>? isPlausibleWord = null)
    {
        _settings = settings;
        _appDataProvider = appDataProvider;
        _logger = logger;
        _isPlausibleWord = isPlausibleWord;

        var candidateDirs = new List<string>
        {
            Path.Combine(_appDataProvider.GetAppDataDirectory(), "models"),
            Path.Combine(AppContext.BaseDirectory, "assets", "models"),
        };

        // 开发辅助：从当前进程目录向上定位仓库根（以 QuickTranslate.sln 为标记），
        // 加载仓库内 assets/models，避免每次发布时复制模型；不再硬编码某台机器的绝对路径。
        // 测试 / CI 等无头环境可设置 QUICKTRANSLATE_DISABLE_DEV_MODEL_PATHS=1
        // 跳过仓库开发路径，确保 MissingModels 等测试的行为可预测。
        const string disableDevEnv = "QUICKTRANSLATE_DISABLE_DEV_MODEL_PATHS";
        var skipDevPaths = Environment.GetEnvironmentVariable(disableDevEnv);
        if (!string.Equals(skipDevPaths, "1", StringComparison.Ordinal))
        {
            var repoModelsDir = TryGetRepoModelsDirectory();
            if (repoModelsDir != null)
            {
                candidateDirs.Add(repoModelsDir);
            }
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

    // 从 AppContext.BaseDirectory 向上最多 8 层查找仓库根（以 QuickTranslate.sln 为标记），
    // 命中则返回仓库内 assets/models 目录；未命中（如发布到仓库外的自包含部署）返回 null。
    private static string? TryGetRepoModelsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir != null && depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickTranslate.sln")))
            {
                return Path.Combine(dir.FullName, "assets", "models");
            }

            dir = dir.Parent;
        }

        return null;
    }

    private bool TryGetDetCache(Bitmap bitmap, PhysicalRect crop, out List<PhysicalRect>? boxes)
    {
        lock (_detCacheLock)
        {
            if (_detCacheBitmap != null
                && ReferenceEquals(_detCacheBitmap, bitmap)
                && _detCacheBoxes != null
                && (DateTime.UtcNow - _detCacheAtUtc).TotalSeconds <= DetCacheTtlSeconds
                && ContainsRect(_detCacheCrop, crop))
            {
                boxes = _detCacheBoxes;
                return true;
            }
        }
        boxes = null;
        return false;
    }

    private static bool ContainsRect(PhysicalRect outer, PhysicalRect inner)
    {
        return inner.X >= outer.X && inner.Y >= outer.Y
            && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    }

    private void SetDetCache(Bitmap bitmap, PhysicalRect crop, List<PhysicalRect> boxes)
    {
        lock (_detCacheLock)
        {
            _detCacheBitmap = bitmap;
            _detCacheCrop = crop;
            _detCacheBoxes = boxes;
            _detCacheAtUtc = DateTime.UtcNow;
        }
    }

    private bool TryGetRecLineCache(Bitmap bitmap, PhysicalRect region, PhysicalRect box, out RecLineCacheEntry? entry)
    {
        lock (_recCacheLock)
        {
            entry = null;
            if (_recCacheBitmap == null
                || !ReferenceEquals(_recCacheBitmap, bitmap)
                || _recCacheRegion != region
                || _recCache == null
                || (DateTime.UtcNow - _recCacheAtUtc).TotalSeconds > DetCacheTtlSeconds)
            {
                return false;
            }

            foreach (var e in _recCache)
            {
                if (BoxIou(box, e.Box) >= RecCacheMinIou)
                {
                    entry = e;
                    return true;
                }
            }
            return false;
        }
    }

    private void SetRecLineCache(Bitmap bitmap, PhysicalRect region, PhysicalRect box, RecLineCacheEntry entry)
    {
        lock (_recCacheLock)
        {
            // 帧换代 → 清空重建（与 det 缓存同生命周期策略）
            if (!ReferenceEquals(_recCacheBitmap, bitmap) || _recCacheRegion != region)
            {
                _recCacheBitmap = bitmap;
                _recCacheRegion = region;
                _recCache = new List<RecLineCacheEntry>();
                _recCacheAtUtc = DateTime.UtcNow;
            }

            // TTL 过期：不逐条清理，下次帧换代时整体重建
            if ((DateTime.UtcNow - _recCacheAtUtc).TotalSeconds > DetCacheTtlSeconds)
            {
                _recCacheAtUtc = DateTime.UtcNow;
                _recCache.Clear();
            }

            // 同行（IoU 容差内）更新而非重复追加
            for (int i = 0; i < _recCache.Count; i++)
            {
                if (BoxIou(box, _recCache[i].Box) >= RecCacheMinIou)
                {
                    _recCache[i] = entry;
                    return;
                }
            }
            _recCache.Add(entry);
        }
    }

    /// <summary>
    /// 计算 det 输入裁剪区（帧局部坐标）：有焦点带时取带 ± 20% 帧高边距，
    /// 边距覆盖超高行跨界与带的少量偏移；裁剪收益不明显（≥95% 帧高）时直接全帧，
    /// 避免裁剪本身的拷贝开销。带加宽重试尽量落在首次裁剪区内（配合包含式缓存）。
    /// </summary>
    private static PhysicalRect ComputeDetCrop(ScreenFrame frame, PhysicalRect? focusBand)
    {
        var full = new PhysicalRect(0, 0, frame.Bitmap.Width, frame.Bitmap.Height);
        if (!focusBand.HasValue) return full;

        int h = frame.Bitmap.Height;
        var fb = focusBand.Value;
        int bandTop = fb.Y - frame.Region.Y;
        int bandBottom = fb.Bottom - frame.Region.Y;
        int margin = h / 5;
        int top = Math.Clamp(bandTop - margin, 0, h);
        int bottom = Math.Clamp(bandBottom + margin, 0, h);
        if (bottom - top <= 0 || bottom - top >= h * 0.95) return full;
        return new PhysicalRect(0, top, frame.Bitmap.Width, bottom - top);
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

    public Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default)
        => RecognizeAsync(frame, null, ct);

    public async Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, PhysicalRect? focusBand, CancellationToken ct = default)
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

            // ===== PREPROCESS + DETECTOR（同帧缓存命中时跳过） =====
            var preprocessStart = sw.Elapsed;
            TimeSpan preprocess = TimeSpan.Zero;
            TimeSpan detectorElapsed = TimeSpan.Zero;
            List<PhysicalRect> detBoxes;
            // 有焦点带时只在「带 ± 20% 帧高」区域跑 det，降低大帧 det 输入面积；
            // 盒子统一平移到帧局部坐标，后续管线不感知裁剪。
            var detCrop = ComputeDetCrop(frame, focusBand);
            bool detCacheHit = TryGetDetCache(frame.Bitmap, detCrop, out var cachedBoxes);
            if (detCacheHit)
            {
                detBoxes = cachedBoxes!;
                _logger.LogDebug("DetCache: hit, reused {Count} det boxes for same frame", detBoxes.Count);
            }
            else
            {
                bool cropped = detCrop.X != 0 || detCrop.Y != 0 ||
                               detCrop.Width != frame.Bitmap.Width || detCrop.Height != frame.Bitmap.Height;
                Bitmap? cropBmp = cropped ? CropBitmap(frame.Bitmap, detCrop) : null;
                var detSource = cropBmp ?? frame.Bitmap;
                float[]? pooledDetInput = null;
                try
                {
                    var (detInput, detScaleW, detScaleH, detInputW, detInputH) = PreprocessDet(detSource);
                    pooledDetInput = detInput;
                    preprocess = sw.Elapsed - preprocessStart;

                    ct.ThrowIfCancellationRequested();

                    var detectorStart = sw.Elapsed;
                    detBoxes = RunDetector(holder.DetSession, detInput, detInputW, detInputH, detScaleW, detScaleH,
                        detSource.Width, detSource.Height);
                    detectorElapsed = sw.Elapsed - detectorStart;

                    // 裁剪区内的盒子平移回帧局部坐标（全帧时起点为 0，等价无操作）
                    if (cropped)
                    {
                        for (int i = 0; i < detBoxes.Count; i++)
                        {
                            var b = detBoxes[i];
                            detBoxes[i] = new PhysicalRect(b.X + detCrop.X, b.Y + detCrop.Y, b.Width, b.Height);
                        }
                    }

                    SetDetCache(frame.Bitmap, detCrop, detBoxes);
                }
                finally
                {
                    // [池化] chw 归还：RunDetector 已返回（其内部 using 已释放 outputs，
                    // NamedOnnxValue/DenseTensor 局部变量随方法返回死亡），数组再无引用。
                    if (pooledDetInput != null) ArrayPool<float>.Shared.Return(pooledDetInput);
                    cropBmp?.Dispose();
                }
            }

            // 诊断日志：检测框原始尺寸（定位框太扁/偏离问题）
            _logger.LogDebug("DetBoxes: count={Count} frameRegion=({RX},{RY},{RW}x{RH})",
                detBoxes.Count, frame.Region.X, frame.Region.Y, frame.Region.Width, frame.Region.Height);
            foreach (var db in detBoxes)
            {
                _logger.LogDebug("  DetBox: ({X},{Y},{W}x{H})", db.X, db.Y, db.Width, db.Height);
            }

            // 焦点带过滤：只识别与焦点带垂直相交的行。
            // Block 截图含大量与目标句子无关的行（工具栏/侧栏/远处代码），全部识别既慢
            // （每行 rec ~100ms）又给块选择引入噪声。焦点带由调用方按光标位置 ± 若干行高给出。
            int recognizedTotal = detBoxes.Count;
            if (focusBand.HasValue)
            {
                var fb = focusBand.Value;
                detBoxes = detBoxes
                    .Where(b => b.Y + frame.Region.Y < fb.Bottom && b.Bottom + frame.Region.Y > fb.Top)
                    .ToList();
                _logger.LogDebug("FocusBand: kept {Kept}/{Total} lines band=({X},{Y},{W}x{H})",
                    detBoxes.Count, recognizedTotal, fb.X, fb.Y, fb.Width, fb.Height);

                // 近邻优先：带内行多于上限时只识别离带中心（≈光标）最近的行，
                // 避免把远处工具栏/侧栏的行也跑一遍 rec。
                if (detBoxes.Count > MaxLinesToRecognize)
                {
                    int centerY = fb.Y + fb.Height / 2 - frame.Region.Y;
                    detBoxes = detBoxes
                        .OrderBy(b => Math.Abs(b.Y + b.Height / 2 - centerY))
                        .Take(MaxLinesToRecognize)
                        .OrderBy(b => b.Y)
                        .ToList();
                    _logger.LogDebug("FocusBand: capped to nearest {Cap} lines", MaxLinesToRecognize);
                }
            }

            ct.ThrowIfCancellationRequested();

            // ===== CLASSIFIER + RECOGNIZER (per box) =====
            // 串行执行：ORT 单次推理已用满 CPU 核（AppendExecutionProvider_CPU 默认 intra-op），
            // 低核数机器上多行并行反而因线程超订导致 RecognizerMs 恶化 3-4 倍（实测 8-16s）。
            // 提速改由焦点带过滤（减少行数）承担。
            // 裁剪在主线程顺序完成（System.Drawing.Bitmap 非线程安全）。
            var lineBmps = new Bitmap?[detBoxes.Count];
            var recCacheHits = 0;
            for (int i = 0; i < detBoxes.Count; i++)
            {
                // 行级缓存命中 → 跳过该行裁剪，rec 循环里直接取缓存结果
                if (TryGetRecLineCache(frame.Bitmap, frame.Region, detBoxes[i], out _))
                {
                    recCacheHits++;
                    continue;
                }
                lineBmps[i] = CropBitmap(frame.Bitmap, detBoxes[i]);
            }
            LastRecCacheHits = recCacheHits;
            if (recCacheHits > 0)
                _logger.LogDebug("RecCache: {Hits}/{Total} lines reused from same-frame line cache", recCacheHits, detBoxes.Count);

            // RecBox = 词框/行框坐标系实际对应的框（原 det 框或主墨水带裁剪后的子框，
            // 帧局部坐标）；词框坐标由它平移而来，组装 OcrLine 时必须用它而非原 det 框。
            var lineResults = new (string? Text, IReadOnlyList<OcrWord> Words, string Strategy, float Angle, float Confidence, PhysicalRect RecBox)[detBoxes.Count];
            var classifierTotal = TimeSpan.Zero;
            var recognizerTotal = TimeSpan.Zero;

            try
            {
                for (int lineIdx = 0; lineIdx < detBoxes.Count; lineIdx++)
                {
                    ct.ThrowIfCancellationRequested();
                    var box = detBoxes[lineIdx];

                    // 行级缓存命中：文本/词框/置信度整体复用，跳过 cls+rec+词框切分
                    if (TryGetRecLineCache(frame.Bitmap, frame.Region, box, out var cachedEntry))
                    {
                        var ce = cachedEntry!;
                        if (ce.Text != null)
                            lineResults[lineIdx] = (ce.Text, ce.Words, ce.Strategy, ce.Angle, ce.Confidence, ce.RecBox);
                        continue;
                    }

                    var lineBmp = lineBmps[lineIdx];
                    if (lineBmp == null) continue;

                    // ===== rec 输入增强：背景色距离灰度化（保留色度对比 + 自动浅底深字极性）=====
                    using var enhancedBmp = EnhanceForRec(lineBmp, out bool darkInverted);
                    if (darkInverted)
                        _logger.LogDebug("RecEnhance: dark background normalized (line {Idx})", lineIdx);

                    // ===== 主墨水带垂直裁剪 =====
                    // det 框高度归一化会把行框撑大，紧凑行距时邻行墨水会渗入裁剪图；
                    // 混入的邻行内容让 cls/rec 读到"一行半"而输出乱码（日志实测：
                    // 'ive commnte'），词框也会被致密的邻行渗漏带劫持压成细条。
                    // 先按行墨水剖面定位本行文字的主导墨水带（InkBandSelector，
                    // 含短密渗漏带防劫持规则），仅对带区（± RecBandPaddingPx）跑 cls/rec。
                    int bandTop = 0, bandBottom = enhancedBmp.Height;
                    var bandRowInk = ComputeRowInkOnLightBackground(enhancedBmp);
                    var dominantBand = InkBandSelector.SelectDominant(
                        bandRowInk, InkBandSelector.DefaultNoiseFloor(enhancedBmp.Height));
                    if (dominantBand.HasValue)
                    {
                        int padTop = Math.Max(0, dominantBand.Value.Top - RecBandPaddingPx);
                        int padBottom = Math.Min(enhancedBmp.Height, dominantBand.Value.Bottom + RecBandPaddingPx);
                        if (padBottom - padTop < enhancedBmp.Height * 9 / 10)
                        {
                            _logger.LogDebug(
                                "RecBandCrop: rows [0,{H}) -> [{T},{B}) (line {Idx})",
                                enhancedBmp.Height, padTop, padBottom, lineIdx);
                            bandTop = padTop;
                            bandBottom = padBottom;
                        }
                    }

                    bool bandCropped = bandTop > 0 || bandBottom < enhancedBmp.Height;
                    var recBox = box;

                    Bitmap? bandBmp = null;
                    Bitmap? orientedBmp = null;
                    try
                    {
                        Bitmap baseSource = enhancedBmp;
                        if (bandCropped)
                        {
                            bandBmp = CropBitmap(
                                enhancedBmp,
                                new PhysicalRect(0, bandTop, enhancedBmp.Width, bandBottom - bandTop));
                            if (bandBmp != null)
                            {
                                baseSource = bandBmp;
                                // 词框坐标系同步收缩：后续投影切分/比例兜底/OcrLine 都以 recBox 为基准
                                recBox = new PhysicalRect(box.X, box.Y + bandTop, box.Width, bandBottom - bandTop);
                            }
                            else
                            {
                                bandCropped = false; // 裁剪失败退回整框
                            }
                        }

                        // ===== CLASSIFIER (optional) =====
                        var clsStart = sw.Elapsed;
                        float clsAngle = 0f;
                        bool clsNeedRotate = false;
                        if (holder.ClsSession != null)
                        {
                            var clsInput = PreprocessCls(baseSource);
                            try
                            {
                                (clsAngle, clsNeedRotate) = RunClassifier(holder.ClsSession, clsInput);
                            }
                            finally
                            {
                                // [池化] chw 归还：RunClassifier 内 using 已释放 outputs，数组再无引用。
                                ArrayPool<float>.Shared.Return(clsInput);
                            }
                        }
                        classifierTotal += sw.Elapsed - clsStart;

                        if (clsNeedRotate) orientedBmp = Rotate180(baseSource);
                        var recSource = orientedBmp ?? baseSource;

                        // ===== RECOGNIZER =====
                        var recStart = sw.Elapsed;
                        var recInput = PreprocessRec(recSource);
                        string recText;
                        float recConfidence;
                        try
                        {
                            (recText, recConfidence) = RunRecognizer(holder.RecSession, recInput, holder.CharDictionary);
                        }
                        finally
                        {
                            // [池化] chw 归还：RunRecognizer 内 using 已释放 outputs，数组再无引用。
                            ArrayPool<float>.Shared.Return(recInput.Input);
                        }
                        recognizerTotal += sw.Elapsed - recStart;

                        string? cacheText = string.IsNullOrWhiteSpace(recText) ? null : recText;
                        if (cacheText == null)
                        {
                            // 空结果同样入缓存：避免带扩展重试时空行再跑一遍 rec
                            SetRecLineCache(frame.Bitmap, frame.Region, box,
                                new RecLineCacheEntry(box, null, Array.Empty<OcrWord>(), "none", clsAngle, recConfidence, recBox));
                            continue;
                        }

                        // 词框解析三级策略（spec 8.3）：
                        // 1) 垂直投影精确切分（含自适应阈值重试 + 多余段合并修复）；
                        // 2) 受约束最优切分（DP 在墨水最少处下刀，处理粘连/噪声）；
                        // 3) 加权比例法兜底（字符区间估计，置信度最低）。
                        // 1/2 由 TrySegmentOrConstrained 串联：位图墨水投影只做一次，
                        // 回退到受约束切分时不再重复全像素遍历。
                        // 注意 localBox 用 recBox（主墨水带裁剪后的子框）而非原 det 框，
                        // 否则词框 X/Y 平移会整体偏移一个 bandTop。
                        IReadOnlyList<OcrWord> words;
                        string wordStrategy;
                        if (ProjectionWordSegmenter.TrySegmentOrConstrained(
                                recSource, recText, recBox, frame.Region, clsNeedRotate, lineIdx,
                                _isPlausibleWord, out var segWords, out var segDetail))
                        {
                            words = segWords;
                            wordStrategy = segDetail == "constrained" ? "constrained" : $"projection({segDetail})";
                        }
                        else
                        {
                            // 比例法兜底的词框需屏幕绝对坐标（与投影/受约束切分一致）
                            var lineScreenBox = new PhysicalRect(
                                recBox.X + frame.Region.X, recBox.Y + frame.Region.Y, recBox.Width, recBox.Height);
                            words = BuildWords(recText, lineScreenBox, lineIdx);
                            wordStrategy = "proportional";
                        }
                        lineResults[lineIdx] = (recText, words, wordStrategy, clsAngle, recConfidence, recBox);
                        SetRecLineCache(frame.Bitmap, frame.Region, box,
                            new RecLineCacheEntry(box, recText, words, wordStrategy, clsAngle, recConfidence, recBox));
                    }
                    finally
                    {
                        orientedBmp?.Dispose();
                        bandBmp?.Dispose();
                    }
                }
            }
            finally
            {
                foreach (var bmp in lineBmps)
                    bmp?.Dispose();
            }

            // 按检测顺序组装行，保证日志与行序稳定
            var lines = new List<OcrLine>();
            for (int lineIdx = 0; lineIdx < detBoxes.Count; lineIdx++)
            {
                var res = lineResults[lineIdx];
                if (res.Text == null) continue;

                // detBoxes 是截图位图内的 0-based 局部坐标（DbPostprocess 按位图尺寸计算）。
                // 而 WordSelector 用屏幕绝对坐标的鼠标比对，若直接返回局部坐标将永远 miss →
                // “未检测到可翻译的单词 / 单词识别不可用”。因此：
                //   - CropBitmap 用局部 box（它相对 frame.Bitmap 裁剪）；
                //   - OcrLine/OcrWord 用平移到屏幕绝对坐标的 screenBox（frame.Region 是屏幕坐标）。
                // 注意用 RecBox（主墨水带裁剪后的实际识别框），词框坐标系与它一致。
                var recBox = res.RecBox;
                var screenBox = new PhysicalRect(
                    recBox.X + frame.Region.X,
                    recBox.Y + frame.Region.Y,
                    recBox.Width,
                    recBox.Height);

                if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    // 逐词明细（文本+屏幕绝对框）：定位"检测/选取位置不准"的关键证据。
                    // Debug 关闭时零开销（IsEnabled 守卫，字符串不构建）。
                    var detail = string.Join(" | ",
                        res.Words.Select(w => $"'{w.Text}'({w.Box.X},{w.Box.Y} {w.Box.Width}x{w.Box.Height})"));
                    _logger.LogDebug(
                        "WordBox: strategy={Strategy} words={Count} confidence={Confidence:F3} line={Idx} lineBox=({X},{Y} {W}x{H}) detail=[{Detail}]",
                        res.Strategy, res.Words.Count, res.Confidence, lineIdx,
                        screenBox.X, screenBox.Y, screenBox.Width, screenBox.Height, detail);
                }
                else
                {
                    _logger.LogDebug("WordBox: strategy={Strategy} words={Count} confidence={Confidence:F3} line={Idx}",
                        res.Strategy, res.Words.Count, res.Confidence, lineIdx);
                }
                lines.Add(new OcrLine(screenBox, res.Words, res.Text, res.Angle, res.Confidence));
            }

            // det 偶尔把相隔大片空白的两处文字合并成一个检测框（如左右两页中间隔空白），
            // 导致行框/选区横跨空白区 → 按词框间大空隙拆成独立行。
            int beforeGapSplit = lines.Count;
            lines = LineGapSplitter.SplitLines(lines);
            if (lines.Count != beforeGapSplit)
                _logger.LogDebug("LineGapSplit: {Before} lines split into {After}", beforeGapSplit, lines.Count);

            // 行首图标单符号清理（?/0/• 等），同步收紧行框改善选区。
            lines = LeadingGlyphCleaner.Clean(lines, out int glyphCleaned);
            if (glyphCleaned > 0)
                _logger.LogDebug("LeadingGlyphClean: removed leading glyph on {Count} lines", glyphCleaned);

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
    // 自适应 det 输入上限：大截图（≥800）用 800 节省 ~30% det 耗时（1200×720 块 → 800 边长面积约 0.44×），
    // 小截图（<800，如 372×80 单词）保持 960 以保留小字号召回。
    private const int DetMaxSideLenLarge = 800;
    private const int DetMaxSideLenSmall = 960;
    private const int AdaptiveThreshold = 800;

    // HWC→CHW 归一化查表：每字节一次浮点乘减与除法预烘焙成 256 项查表，
    // 转换循环退化为纯内存拷贝，大帧预处理从 ~20ms 降到 ~5ms。
    private static readonly float[] DetLutB;
    private static readonly float[] DetLutG;
    private static readonly float[] DetLutR;

    static PaddleOcrV6Engine()
    {
        DetLutB = new float[256];
        DetLutG = new float[256];
        DetLutR = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float v = i / 255f;
            DetLutB[i] = (v - DetMean[0]) / DetStd[0];
            DetLutG[i] = (v - DetMean[1]) / DetStd[1];
            DetLutR[i] = (v - DetMean[2]) / DetStd[2];
        }
    }

    private static (float[] Input, float ScaleW, float ScaleH, int InputW, int InputH) PreprocessDet(Bitmap src)
    {
        int srcW = src.Width, srcH = src.Height;

        // 自适应上限：大截图 (max(srcW,srcH)≥800) 用 800，小截图用 960。
        // 大 1200×720 块 → 800 上限 det 输入面积 800×480=384k vs 960×576=553k，节省 ~30%；
        // 小 372×80 单词保持 960 时不降采样，保留小字号召回。
        // Resize so long side <= limit, keeping aspect ratio
        // 与上游 PaddleOCR 一致：只把大图缩放到边长上限，小帧保持原生分辨率（ratio ≤ 1）。
        // 旧实现会把小截图放大到长边 960（Word 起捕 300x100 → 960x320，面积约 10 倍），
        // det 每次多付 ~300ms；放大不带来任何信息量，只会增加计算量。
        int limit = (srcW >= AdaptiveThreshold || srcH >= AdaptiveThreshold) ? DetMaxSideLenLarge : DetMaxSideLenSmall;
        float ratio = Math.Min(1f, Math.Min((float)limit / srcW, (float)limit / srcH));
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

        // [池化] byte 租借：仅方法内生命周期，LockBits 拷贝进 chw 后即还池。
        // det 输入最大 ~960×992×4 ≈ 3.8MB，此前每次热键全新分配，是 Gen0 压力主源之一。
        // 注意 Marshal.Copy 长度用精确像素字节数而非 Rent 后的数组长度。
        var bytes = ArrayPool<byte>.Shared.Rent(inputW * inputH * 4);
        float[] chw;
        try
        {
            var bmpData = resized.LockBits(new Rectangle(0, 0, inputW, inputH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(bmpData.Scan0, bytes, 0, inputW * inputH * 4);
            }
            finally
            {
                resized.UnlockBits(bmpData);
            }

            // HWC (BGRA32) -> CHW，通道顺序 BGR（与 PaddleOCR DecodeImage img_mode=BGR 一致）。
            // 注意：PaddleOCR 对 BGR 图像直接按 [0.485, 0.456, 0.406] 逐通道归一化，
            // 即 B 通道用 0.485、R 通道用 0.406（历史怪癖，勿"修正"成 RGB 顺序，
            // 否则与模型训练分布不符，检测框召回率明显下降）。
            // 单遍线性循环 + 查表：字节序与三平面索引同步递增，无乘法索引重算；
            // 归一化预烘焙为 LUT，内循环仅剩 3 次查表写入。
            // [池化] chw 租借：数组逃逸给 RunDetector→DenseTensor（零拷贝包装，
            // 允许超长后备数组），由调用方在 session.Run 完成后归还
            // （见 RecognizeAsync det 分支的 pooledDetInput finally）。
            chw = ArrayPool<float>.Shared.Rent(3 * inputH * inputW);
            int hw = inputH * inputW;
            int plane2 = 2 * hw;
            int srcIdx = 0;
            for (int chwIdx = 0; chwIdx < hw; chwIdx++, srcIdx += 4)
            {
                chw[chwIdx] = DetLutB[bytes[srcIdx]];
                chw[hw + chwIdx] = DetLutG[bytes[srcIdx + 1]];
                chw[plane2 + chwIdx] = DetLutR[bytes[srcIdx + 2]];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
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

        // [池化] input 为 ArrayPool 租借（可能超长）。DenseTensor 对后备内存做严格等长校验，
        // 故用 AsMemory 切片到精确张量长度后零拷贝包装。
        var tensor = new DenseTensor<float>(input.AsMemory(0, 3 * inputH * inputW), dims);
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

    internal static List<PhysicalRect> DbPostprocess(
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

        // 形态学闭运算（先膨胀后腐蚀，3×3 一轮）：桥接细体字/小字号在低分辨率
        // 缩放下的 ≤2px 笔画断缝，避免连通域提取把同一行切成多个碎片。
        // 闭运算不改变大连通域外轮廓；行间空隙通常 ≥6px（输入尺度），不会粘连相邻行。
        mask = CloseMask(mask, outW, outH);

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
        // 水平间距约束：仅限同一行内的分片（词间空隙级别）才允许合并；
        // 无约束时垂直范围重叠的两处分离文字（分栏/被图隔开的两段文字）会被
        // 链式合并成横跨大片空白的巨型框，导致选区“中间断开却连通到附近其他文本”。
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
                if (vInt <= vMin * 0.3)
                    continue;

                // 同行分片水平上必然邻近：允许的水平空隙随行高缩放
                // （词间空隙约 0.2-0.5 倍行高，0.5 上限留足 unclip 后的余量）。
                int hGap = b.X - m.Right;
                int maxMergeGap = Math.Max(6, (int)Math.Round(vMin * 0.5));
                if (hGap > maxMergeGap)
                    continue;

                // Same line → union
                int nx1 = Math.Min(m.X, b.X);
                int ny1 = Math.Min(m.Y, b.Y);
                int nx2 = Math.Max(m.Right, b.Right);
                int ny2 = Math.Max(m.Bottom, b.Bottom);
                merged[i] = new PhysicalRect(nx1, ny1, nx2 - nx1, ny2 - ny1);
                absorbed = true;
                break;
            }
            if (!absorbed) merged.Add(b);
        }
        boxes = merged;

        // Final vertical fine-tune: DB 框贴字形笔画，缺 ascender/descender 余量，
        // 按盒子自身高度上下比例扩展（并带绝对像素下限）。
        // 历史教训：旧实现按固定帧高比例强制 clamp 盒高（900px 帧 → 至少 32px），
        // 小字体（12-14px 代码）被硬撑高后 crop 吃进相邻行污染 rec 输入，
        // 大标题又被上限压扣；比例扩展两头不失真。
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            int pad = Math.Max(2, (int)Math.Round(b.Height * 0.25));
            int ny1 = Math.Max(0, b.Y - pad);
            int ny2 = Math.Min(origH, b.Bottom + pad);
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

    /// <summary>
    /// 3×3 形态学闭运算（先膨胀后腐蚀，一轮）：填充 ≤2px 的细缝而不改变大区域外轮廓。
    /// 膨胀时越界按背景处理（区域只向内生长）；腐蚀时越界按前景处理，
    /// 避免贴边区域被削掉 1px。
    /// </summary>
    internal static byte[] CloseMask(byte[] mask, int w, int h)
    {
        var dilated = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                bool on = false;
                for (int dy = -1; dy <= 1 && !on; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    int nrow = ny * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        if (mask[nrow + nx] == 1) { on = true; break; }
                    }
                }
                dilated[row + x] = on ? (byte)1 : (byte)0;
            }
        }

        var closed = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                bool all = true;
                for (int dy = -1; dy <= 1 && all; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue; // 越界视为前景
                    int nrow = ny * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue; // 越界视为前景
                        if (dilated[nrow + nx] == 0) { all = false; break; }
                    }
                }
                closed[row + x] = all ? (byte)1 : (byte)0;
            }
        }
        return closed;
    }

    // ==================== CLASSIFIER ====================

    private static readonly float[] ClsMean = new[] { 0.5f, 0.5f, 0.5f };
    private static readonly float[] ClsStd = new[] { 0.5f, 0.5f, 0.5f };
    // 官方 cls_image_shape = 3x48x192（ch_ppocr_mobile cls 固定输入）。
    // 等比缩放到高 48，宽不足 192 右侧补黑——勿用正方形 letterbox，
    // 否则与导出模型的固定输入 shape 不符或产生拉伸失真。
    private const int ClsW = 192;
    private const int ClsH = 48;

    private static float[] PreprocessCls(Bitmap src)
    {
        float ratio = (float)ClsH / src.Height;
        int dw = Math.Clamp((int)Math.Round(src.Width * ratio), 1, ClsW);

        using var resized = new Bitmap(ClsW, ClsH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.Clear(Color.Black);
            g.DrawImage(src, new Rectangle(0, 0, dw, ClsH), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }

        // [池化] 固定尺寸 192×48：byte 仅方法内使用；chw 租借后逃逸给 RunClassifier，
        // 由调用方在推理完成后归还（cls 逐行串行执行，池命中率高）。
        var bytes = ArrayPool<byte>.Shared.Rent(ClsW * ClsH * 4);
        float[] chw;
        try
        {
            var bmpData = resized.LockBits(new Rectangle(0, 0, ClsW, ClsH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(bmpData.Scan0, bytes, 0, ClsW * ClsH * 4); }
            finally { resized.UnlockBits(bmpData); }

            chw = ArrayPool<float>.Shared.Rent(3 * ClsH * ClsW);
            int hw = ClsH * ClsW;
            for (int i = 0; i < hw; i++)
            {
                int bi = i * 4;
                byte b = bytes[bi], g = bytes[bi + 1], r = bytes[bi + 2];
                chw[i] = ((r / 255f) - ClsMean[0]) / ClsStd[0];
                chw[hw + i] = ((g / 255f) - ClsMean[1]) / ClsStd[1];
                chw[2 * hw + i] = ((b / 255f) - ClsMean[2]) / ClsStd[2];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
        return chw;
    }

    private static (float Angle, bool NeedRotate) RunClassifier(InferenceSession session, float[] input)
    {
        var inputName = session.InputMetadata.Keys.First();
        var dims = new[] { 1, 3, ClsH, ClsW };
        // [池化] 同 RunDetector：租借数组可能超长，切片到精确张量长度后零拷贝包装。
        var tensor = new DenseTensor<float>(input.AsMemory(0, 3 * ClsH * ClsW), dims);
        var inputValues = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var outputs = session.Run(new[] { inputValues });
        var arr = outputs.First().AsTensor<float>().ToArray();

        // Output shape [1, 2]; label 0 = 0 deg, label 1 = 180 deg
        if (arr.Length < 2) return (0f, false);
        // Softmax threshold：与官方 cls_thresh=0.9 对齐，仅高置信倒置才翻转，
        // 避免正常行被误判 180° 导致整行识别失败
        float sum = (float)(Math.Exp(arr[0]) + Math.Exp(arr[1]));
        float p0 = (float)Math.Exp(arr[0]) / sum;
        float p1 = (float)Math.Exp(arr[1]) / sum;
        const float rotateThresh = 0.9f;
        bool need = p1 > rotateThresh && p1 > p0;
        return (need ? 180f : 0f, need);
    }

    // ==================== RECOGNIZER ====================

    private static readonly float[] RecMean = new[] { 0.5f, 0.5f, 0.5f };
    private static readonly float[] RecStd = new[] { 0.5f, 0.5f, 0.5f };
    private const int RecH = 48;
    private const int RecMinW = 48;
    // 模型导出为动态宽度（TRT 配置最大 3200）。官方静态推理用 320 + 补黑，
    // 但超宽行压缩到 320 会失真；GitHub 项目描述类长行 839×17 → naturalW 2369
    // 在 1280 下被压缩 0.54 倍导致丢字（日志 520,1070,839×17 → kdaeade 0.473），
    // 放宽至 2560 保证 1200 宽以内的段落行不失真（仍 <3200 上限）。
    private const int RecMaxW = 2560;

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

        // [池化] byte 仅方法内使用；chw 租借后逃逸给 RunRecognizer，
        // 由调用方在推理完成后归还（rec 逐行串行执行，池命中率高）。
        // rec 宽度可变（48..1280，/4 对齐），ArrayPool 按容量分桶租借天然适配。
        var bytes = ArrayPool<byte>.Shared.Rent(targetW * RecH * 4);
        float[] chw;
        try
        {
            var bmpData = resized.LockBits(new Rectangle(0, 0, targetW, RecH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(bmpData.Scan0, bytes, 0, targetW * RecH * 4); }
            finally { resized.UnlockBits(bmpData); }

            int hw = RecH * targetW;
            chw = ArrayPool<float>.Shared.Rent(3 * hw);
            for (int i = 0; i < hw; i++)
            {
                int bi = i * 4;
                byte b = bytes[bi], g = bytes[bi + 1], r = bytes[bi + 2];
                chw[i] = ((r / 255f) - RecMean[0]) / RecStd[0];
                chw[hw + i] = ((g / 255f) - RecMean[1]) / RecStd[1];
                chw[2 * hw + i] = ((b / 255f) - RecMean[2]) / RecStd[2];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
        return (chw, targetW);
    }

    private static (string Text, float Confidence) RunRecognizer(InferenceSession session, (float[] Input, int Width) input, string[] dict)
    {
        var (data, w) = input;
        var inputName = session.InputMetadata.Keys.First();
        var dims = new[] { 1, 3, RecH, w };
        // [池化] 同 RunDetector：租借数组可能超长，切片到精确张量长度后零拷贝包装。
        var tensor = new DenseTensor<float>(data.AsMemory(0, 3 * RecH * w), dims);
        var inputValues = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var outputs = session.Run(new[] { inputValues });

        var outTensor = outputs.First().AsTensor<float>();
        var outShape = outTensor.Dimensions.ToArray();
        // Expected [1, T, C] where C = num_classes + 1 (blank at end)
        int T, C;
        if (outShape.Length == 3) { T = outShape[1]; C = outShape[2]; }
        else if (outShape.Length == 2) { T = outShape[0]; C = outShape[1]; }
        else return (string.Empty, 0f);

        var probs = outTensor.ToArray();

        return CtcGreedyDecode(probs, T, C, dict);
    }

    internal static (string Text, float Confidence) CtcGreedyDecode(float[] probs, int T, int C, string[] dict)
    {
        // PaddleOCR CTC 布局：blank 固定在 index 0，字典字符从 1 开始，空格在末位 C-1。
        // dict 数组由 BuildCharDictionary 按同一布局构建（dict[i] 即 label i 的字符）。
        int blankIdx = 0;
        var sb = new System.Text.StringBuilder();
        int prevIdx = -1;

        // 置信度 = 实际输出字符的时间步平均概率（PaddleOCR 同口径）。
        // 部分 ONNX 导出图内含 softmax（行和≈1），部分输出原始 logits；
        // 以首行检测：未归一化时对命中时间步单独做 softmax 取概率。
        bool normalized = IsProbabilityRow(probs, C);
        double confSum = 0;
        int confCount = 0;

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
                    float p = normalized ? bestVal : SoftmaxAt(probs, start, C, bestIdx);
                    confSum += p;
                    confCount++;
                }
            }
            prevIdx = bestIdx;
        }

        float confidence = confCount > 0 ? (float)(confSum / confCount) : 0f;
        return (sb.ToString().Trim(), confidence);
    }

    private static bool IsProbabilityRow(float[] probs, int C)
    {
        if (probs.Length < C) return false;
        double sum = 0;
        for (int c = 0; c < C; c++) sum += probs[c];
        return Math.Abs(sum - 1.0) < 0.05;
    }

    private static float SoftmaxAt(float[] probs, int start, int C, int idx)
    {
        // 行内减最大值防溢出；仅在输出未归一化（logits）时调用
        float max = float.MinValue;
        for (int c = 0; c < C; c++)
        {
            float v = probs[start + c];
            if (v > max) max = v;
        }
        double sumExp = 0;
        for (int c = 0; c < C; c++) sumExp += Math.Exp(probs[start + c] - max);
        return (float)(Math.Exp(probs[start + idx] - max) / sumExp);
    }

    // ==================== UTILS ====================

    /// <summary>
    /// rec/cls 输入增强：背景色距离灰度化。
    /// 旧实现按 BT.601 亮度做灰度：对"彩色文字/渐变底"（文字与背景亮度接近、
    /// 色度差异大）会把对比度抹到接近零，rec 只能输出乱码（日志实测：营销页彩色
    /// 标题整行乱认 GtHub/tols/omunty，且随截图亚像素相位随机波动——同一区域
    /// 多次按压时好时坏）。
    /// 新实现：以逐通道中位数估计背景色（背景像素在行裁剪中占绝对多数，中位数稳定），
    /// 每像素取与背景色的最大通道差 d = max(|r-br|,|g-bg|,|b-bb|)，按鲁棒峰值
    /// （d 的 98 分位）拉伸后反转输出——d 大（离背景远 = 文字）输出深色：
    ///   1) 同时保留亮度对比与色度对比，彩色文字不再被抹掉；
    ///   2) 天然保证"浅底深字"极性，暗色主题无需显式反色分支；
    ///   3) ClearType 彩边在色距下同样表现为"离背景远"，去彩边目标保持。
    /// inverted 仅作诊断语义保留：true = 源裁剪为深色背景（均值亮度 &lt; 127）。
    /// </summary>
    internal static Bitmap EnhanceForRec(Bitmap src, out bool inverted)
    {
        int w = src.Width, h = src.Height;
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        inverted = false;

        var srcData = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[]? srcBytes = null;
        byte[]? dist = null;
        byte[]? dstBytes = null;
        try
        {
            int srcLen = srcData.Stride * h;
            srcBytes = ArrayPool<byte>.Shared.Rent(srcLen);
            Marshal.Copy(srcData.Scan0, srcBytes, 0, srcLen);

            // 第一遍：RGB 逐通道直方图（背景色中位数）+ 亮度均值（inverted 诊断标志）
            int total = w * h;
            var histB = new int[256];
            var histG = new int[256];
            var histR = new int[256];
            long lumaSum = 0;
            for (int i = 0; i < total; i++)
            {
                int j = i * 4;
                byte b = srcBytes[j], g = srcBytes[j + 1], r = srcBytes[j + 2];
                histB[b]++;
                histG[g]++;
                histR[r]++;
                lumaSum += (77 * r + 150 * g + 29 * b) >> 8;
            }

            byte bgB = MedianOf(histB, total);
            byte bgG = MedianOf(histG, total);
            byte bgR = MedianOf(histR, total);
            inverted = (double)lumaSum / total < 127;

            // 第二遍：色距 d = max(|r-br|,|g-bg|,|b-bb|) + d 直方图（dist 池化复用）
            dist = ArrayPool<byte>.Shared.Rent(total);
            var histD = new int[256];
            for (int i = 0; i < total; i++)
            {
                int j = i * 4;
                int db = Math.Abs(srcBytes[j] - bgB);
                int dg = Math.Abs(srcBytes[j + 1] - bgG);
                int dr = Math.Abs(srcBytes[j + 2] - bgR);
                int d = Math.Max(db, Math.Max(dg, dr));
                if (d > 255) d = 255;
                dist[i] = (byte)d;
                histD[d]++;
            }

            // 鲁棒拉伸上限：d 的 98 分位（避免孤立噪点把上限撑爆导致文字变浅）；
            // 过小（<16）说明整幅近乎单色（无文字），直接输出纯浅底。
            long p98Target = (long)(total * 0.98);
            int p98 = 0;
            long acc = 0;
            for (int v = 0; v < 256; v++)
            {
                acc += histD[v];
                if (acc >= p98Target)
                {
                    p98 = v;
                    break;
                }
            }

            float scale = p98 >= 16 ? 255f / p98 : 0f;

            // 第三遍：写回灰度。out = 255 − min(255, d×scale)：
            // 背景（d≈0）→ 浅 255；文字（d 大）→ 深。任何源极性统一为浅底深字。
            int dstLen = dstData.Stride * h;
            dstBytes = ArrayPool<byte>.Shared.Rent(dstLen);
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * dstData.Stride;
                int gOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int v = 255 - (int)Math.Min(255f, dist[gOff + x] * scale);
                    byte val = (byte)v;
                    int i = rowOff + x * 4;
                    dstBytes[i] = val;
                    dstBytes[i + 1] = val;
                    dstBytes[i + 2] = val;
                    dstBytes[i + 3] = 255;
                }
            }
            Marshal.Copy(dstBytes, 0, dstData.Scan0, dstLen);
        }
        finally
        {
            if (srcBytes != null) ArrayPool<byte>.Shared.Return(srcBytes);
            if (dist != null) ArrayPool<byte>.Shared.Return(dist);
            if (dstBytes != null) ArrayPool<byte>.Shared.Return(dstBytes);
            src.UnlockBits(srcData);
            dst.UnlockBits(dstData);
        }
        return dst;
    }

    /// <summary>直方图中位数（下中位）：累计计数首次达到总数一半时的桶值。</summary>
    private static byte MedianOf(int[] hist, int total)
    {
        long half = (long)(total + 1) / 2;
        long acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= half)
                return (byte)v;
        }
        return 255;
    }

    /// <summary>
    /// 统计浅底深字灰度图（EnhanceForRec 输出已保证极性）每行暗像素数，
    /// 供主墨水带选择（<see cref="InkBandSelector"/>）。阈值 128：
    /// 背景被归一化为浅色（≥128），文字为深色（&lt;128）。
    /// </summary>
    private static int[] ComputeRowInkOnLightBackground(Bitmap gray)
    {
        int w = gray.Width, h = gray.Height;
        var rowInk = new int[h];
        var data = gray.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * data.Stride;
                int count = 0;
                for (int x = 0; x < w; x++)
                {
                    if (bytes[rowOff + x * 4] < 128) count++;
                }
                rowInk[y] = count;
            }
        }
        finally
        {
            gray.UnlockBits(data);
        }
        return rowInk;
    }

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
                // 比例法框为估算值，置信度低于投影/受约束切分（0.9/0.8），
                // 让选择层的 ConfidenceFloor 可感知框的可信度。
                words.Add(new OcrWord(wbox, c.ToString(), 0.6f, lineIdx));
                cursorX = x2;
                i++;
            }
            else
            {
                // Group a run of non-space non-CJK, then split at script/punctuation
                // boundaries（如 "MinSegmentWidth;" → "MinSegmentWidth" + ";"），
                // 避免选取框覆盖到紧贴的标点。
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && !IsCjk(text[i]))
                {
                    i++;
                }
                string chunk = text.Substring(start, i - start);
                if (chunk.Length <= 0) continue;

                var runs = TextRunSplitter.Split(chunk);
                foreach (var run in runs)
                {
                    int wordUnits = 0;
                    for (int ci = run.Start; ci < run.Start + run.Length; ci++)
                        wordUnits += IsWide(chunk[ci]) ? 2 : 1;
                    wordUnits = Math.Max(1, wordUnits);

                    int wordW = unitW * wordUnits;
                    if (remainder > 0) { wordW += Math.Min(remainder, wordUnits); remainder -= Math.Min(remainder, wordUnits); }
                    int x1 = cursorX;
                    int x2 = Math.Min(lineBox.Right, x1 + wordW);
                    int wordBoxW = Math.Max(1, x2 - x1);
                    // 在框内再收缩 3% 左右的左右边距，避免贴到邻词/框边缘
                    int pad = (int)Math.Round(wordBoxW * 0.03);
                    int nx1 = x1 + pad;
                    int nx2 = x2 - pad;
                    if (nx2 - nx1 < 2) { nx1 = x1; nx2 = x2; }
                    var wbox = new PhysicalRect(nx1, lineBox.Y, Math.Max(1, nx2 - nx1), lineBox.Height);
                    words.Add(new OcrWord(wbox, run.Slice(chunk), 0.6f, lineIdx));
                    cursorX = x2;
                }
            }
        }
        return words;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initTask == null || !_initTask.IsValueCreated)
            return;

        var task = _initTask.Value;
        if (task.IsCompletedSuccessfully)
        {
            DisposeSessions(task.Result);
            return;
        }

        if (!task.IsCompleted)
        {
            // 预热仍在进行：绝不阻塞等待 .Result（UI 线程死锁风险——App 退出路径会在
            // STA 线程调用本方法）。挂接延续，在初始化完成后释放原生 ONNX 会话。
            _ = task.ContinueWith(
                static t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result != null)
                        DisposeSessions(t.Result);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        // faulted/canceled：会话未成功创建，无资源需要在此释放。
    }

    private static void DisposeSessions(InferenceSessionsHolder holder)
    {
        holder.DetSession?.Dispose();
        holder.ClsSession?.Dispose();
        holder.RecSession?.Dispose();
    }

    private class InferenceSessionsHolder
    {
        public InferenceSession? DetSession;
        public InferenceSession? ClsSession;
        public InferenceSession? RecSession;
        public string[] CharDictionary = Array.Empty<string>();
    }
}
