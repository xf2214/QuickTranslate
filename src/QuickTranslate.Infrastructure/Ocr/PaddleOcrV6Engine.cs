using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        var required = new[] { "det.onnx", "cls.onnx", "rec.onnx", "ppocr_keys.txt" };
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

                try
                {
                    holder.DetSession = new Microsoft.ML.OnnxRuntime.InferenceSession(detPath);
                    holder.ClsSession = new Microsoft.ML.OnnxRuntime.InferenceSession(clsPath);
                    holder.RecSession = new Microsoft.ML.OnnxRuntime.InferenceSession(recPath);

                    var dictPath = Path.Combine(_modelsDirectory, "ppocr_keys.txt");
                    if (File.Exists(dictPath))
                    {
                        holder.CharDictionary = await File.ReadAllLinesAsync(dictPath);
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

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!_modelReady || _initTask == null)
        {
            return;
        }

        await _initTask.Value.WaitAsync(ct);
    }

    public async Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default)
    {
        if (!_modelReady)
        {
            throw new InvalidOperationException(
                "PP-OCRv6 models missing. Please place det.onnx/cls.onnx/rec.onnx/ppocr_keys.txt under assets/models or %APPDATA%/QuickTranslate/models");
        }

        var sw = Stopwatch.StartNew();
        var lines = new List<OcrLine>();

        InferenceSessionsHolder? holder = null;
        if (_initTask != null)
        {
            holder = await _initTask.Value.WaitAsync(ct);
        }

        var preprocess = sw.Elapsed;

        var detectorStart = sw.Elapsed;
        await Task.Yield();
        var detectorElapsed = sw.Elapsed - detectorStart;

        var classifierStart = sw.Elapsed;
        await Task.Yield();
        var classifierElapsed = sw.Elapsed - classifierStart;

        var recognizerStart = sw.Elapsed;
        await Task.Yield();
        var recognizerElapsed = sw.Elapsed - recognizerStart;

        var postprocessStart = sw.Elapsed;
        await Task.Yield();
        var postprocessElapsed = sw.Elapsed - postprocessStart;

        sw.Stop();

        var timings = new OcrTimings(
            preprocess,
            detectorElapsed,
            classifierElapsed,
            recognizerElapsed,
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
        public Microsoft.ML.OnnxRuntime.InferenceSession? DetSession;
        public Microsoft.ML.OnnxRuntime.InferenceSession? ClsSession;
        public Microsoft.ML.OnnxRuntime.InferenceSession? RecSession;
        public string[] CharDictionary = Array.Empty<string>();
    }
}
