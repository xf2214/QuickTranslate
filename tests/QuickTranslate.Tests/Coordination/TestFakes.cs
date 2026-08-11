using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Common;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Tests.Coordination;

public class FakeHotkeyBroker : IHotkeyBroker
{
    public event EventHandler<HotkeyEvent>? HotkeyFired;

    public List<AppSettings> RegisterDefaultsCalls { get; } = new();
    public List<(HotkeyCombo Word, HotkeyCombo Block)> UpdateCalls { get; } = new();
    public int UnregisterAllCount { get; private set; }

    public void RegisterDefaultsFromSettings(AppSettings settings)
        => RegisterDefaultsCalls.Add(settings);

    public Result TryUpdateHotkeys(HotkeyCombo newWord, HotkeyCombo newBlock)
    {
        UpdateCalls.Add((newWord, newBlock));
        return Result.Ok();
    }

    public void UnregisterAll() => UnregisterAllCount++;

    public void RaiseHotkeyFired(HotkeyEventType type)
        => HotkeyFired?.Invoke(this, new HotkeyEvent(type, DateTimeOffset.Now));
}

public class FakeAppLifecycle : IAppLifecycle
{
    public bool IsPaused { get; private set; }
    public event EventHandler? Paused;
    public event EventHandler? Resumed;
    public event EventHandler<int>? ShuttingDown;

    public void Pause() { IsPaused = true; Paused?.Invoke(this, EventArgs.Empty); }
    public void Resume() { IsPaused = false; Resumed?.Invoke(this, EventArgs.Empty); }
    public void Shutdown(int exitCode = 0) => ShuttingDown?.Invoke(this, exitCode);
    public void SetPaused(bool paused) => IsPaused = paused;
}

public class FakeCursorService : ICursorService
{
    public PhysicalPoint CursorPos { get; set; } = new(100, 100);
    public MonitorId OutMonitorId { get; set; } = new(new IntPtr(1), @"\\.\DISPLAY1");

    public PhysicalPoint GetPhysicalCursorPos(out MonitorId monitorId)
    {
        monitorId = OutMonitorId;
        return CursorPos;
    }
}

public class FakeMonitorService : IMonitorService
{
    public MonitorInfo Primary { get; set; } = new(
        Id: new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"),
        DeviceName: @"\\.\DISPLAY1",
        Bounds: new PhysicalRect(0, 0, 1920, 1080),
        WorkArea: new PhysicalRect(0, 0, 1920, 1040),
        DpiX: 96,
        DpiY: 96,
        IsPrimary: true);

    public Func<IntPtr, MonitorId>? MonitorFromWindowFunc { get; set; }

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => new[] { Primary };

    public MonitorInfo? TryGetMonitorFromPoint(PhysicalPoint pt) => Primary;

    public MonitorInfo? TryGetPrimary() => Primary;

    public MonitorId MonitorFromWindow(IntPtr hwnd)
    {
        if (MonitorFromWindowFunc != null) return MonitorFromWindowFunc(hwnd);
        return Primary.Id;
    }
}

public class FakeScreenCapture : IScreenCapture
{
    public TaskCompletionSource<ScreenFrame> CaptureAroundTcs { get; set; } = new();
    public int CaptureAroundCount { get; private set; }

    public Task<ScreenFrame> CaptureAroundAsync(PhysicalPoint anchor, PhysicalSize size, CancellationToken ct = default)
    {
        CaptureAroundCount++;
        return CaptureAroundTcs.Task;
    }

    public Task<ScreenFrame> CaptureRectAsync(PhysicalRect region, MonitorId? monitorHint = null, CancellationToken ct = default)
    {
        return Task.FromResult(CreateFrame(region, monitorHint ?? MonitorId.Empty));
    }

    public static ScreenFrame CreateFrame(PhysicalRect region, MonitorId monitorId)
    {
        var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), PixelFormat.Format32bppArgb);
        return new ScreenFrame(bmp, region, monitorId);
    }
}

public class FakeOcrEngine : IOcrEngine
{
    public TaskCompletionSource<OcrLayoutResult> RecognizeTcs { get; set; } = new();
    public int RecognizeCount { get; private set; }

    public string EngineName => "FakeOcr";
    public bool IsAvailable => true;
    public event EventHandler? SessionCreated;

    public Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default)
    {
        RecognizeCount++;
        return RecognizeTcs.Task;
    }

    public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;

    public static OcrLayoutResult CreateResult(PhysicalRect captureRegion)
    {
        return new OcrLayoutResult(
            CaptureRegion: captureRegion,
            Lines: Array.Empty<OcrLine>(),
            Timings: new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            CaptureTime: DateTimeOffset.Now,
            DpiX: 96,
            DpiY: 96,
            EngineName: "FakeOcr");
    }
}

public class FakeWordSelector : IWordSelector
{
    public Func<OcrLayoutResult, PhysicalPoint, SelectionOptions?, SelectionResult>? SelectFunc { get; set; }

    public SelectionResult SelectWord(OcrLayoutResult ocr, PhysicalPoint anchor, SelectionOptions? opts = null)
    {
        if (SelectFunc != null) return SelectFunc(ocr, anchor, opts);
        return new SelectionResult(
            Text: "hello",
            ContextLine: "hello world",
            Box: new PhysicalRect(100, 100, 50, 20),
            Kind: SelectionKind.Word,
            Confidence: 0.95f,
            OperationId: Guid.NewGuid(),
            NoTextFound: false);
    }
}

public class FakeOverlayService : ISelectionOverlayService
{
    public Dictionary<MonitorId, int> ShowCountByMonitor { get; } = new();
    public int ShowTotalCount { get; private set; }
    public int UpdateCount { get; private set; }
    public int HideCount { get; private set; }
    public int HideAllCount { get; private set; }
    public List<(PhysicalRect Box, MonitorId Monitor, uint DpiX, uint DpiY)> ShowCalls { get; } = new();

    public void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96)
    {
        ShowTotalCount++;
        ShowCountByMonitor.TryGetValue(monitorId, out var c);
        ShowCountByMonitor[monitorId] = c + 1;
        ShowCalls.Add((physicalBox, monitorId, dpiX, dpiY));
    }

    public void Update(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96)
        => UpdateCount++;

    public void Hide(MonitorId monitorId) => HideCount++;

    public void HideAll() => HideAllCount++;

    public IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect()
        => new Dictionary<MonitorId, (IntPtr, DipRect)>();
}

public class FakeTranslationRouter : ITranslationRouter
{
    public TaskCompletionSource<TranslationResult> TranslateWordTcs { get; set; } = new();
    public TaskCompletionSource<TranslationResult> TranslateBlockTcs { get; set; } = new();
    public int TranslateWordCount { get; private set; }
    public int TranslateBlockCount { get; private set; }

    public Task<TranslationResult> TranslateWordAsync(string word, string targetLang, CancellationToken ct = default)
    {
        TranslateWordCount++;
        return TranslateWordTcs.Task;
    }

    public Task<TranslationResult> TranslateBlockAsync(string blockText, string targetLang, CancellationToken ct = default)
    {
        TranslateBlockCount++;
        return TranslateBlockTcs.Task;
    }

    public static TranslationResult CreateResult(string source, string targetLang)
    {
        return new TranslationResult(
            NormalizedKey: source.ToLowerInvariant(),
            SourceText: source,
            TargetText: "translated-" + source,
            TargetLanguage: targetLang,
            FromCache: false,
            FromDictionary: false,
            NeedsOnline: false);
    }
}

public class FakePopupService : IWordPopupService
{
    public int ShowCount { get; private set; }
    public int HideCount { get; private set; }
    public int HideAllCount { get; private set; }
    public int ShowErrorCount { get; private set; }
    public List<(SelectionResult Sel, TranslationResult Trans, MonitorId Monitor, PhysicalRect Box)> ShowCalls { get; } = new();
    public List<(MonitorId Monitor, PhysicalRect Box, uint DpiX, uint DpiY, string Msg, Guid OpId)> ShowErrorCalls { get; } = new();

    public void Show(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX = 96, uint dpiY = 96)
    {
        ShowCount++;
        ShowCalls.Add((selection, translation, monitorId, anchorBox));
    }

    public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
    {
        ShowErrorCount++;
        ShowErrorCalls.Add((monitorId, anchorBox, dpiX, dpiY, shortMessage, operationId));
    }

    public void Hide() => HideCount++;
    public void HideAll() => HideAllCount++;
}

public class FakeBlockPopupService : IBlockPopupService
{
    public int ShowCount { get; private set; }
    public int HideAllCount { get; private set; }
    public int ShowErrorCount { get; private set; }
    public List<(BlockSelectionResult Block, TranslationResult Trans, MonitorId Monitor, PhysicalRect Box, uint DpiX, uint DpiY)> ShowCalls { get; } = new();
    public List<(MonitorId Monitor, PhysicalRect Box, uint DpiX, uint DpiY, string Msg, Guid OpId)> ShowErrorCalls { get; } = new();

    public void Show(BlockSelectionResult blockSelection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY)
    {
        ShowCount++;
        ShowCalls.Add((blockSelection, translation, monitorId, anchorBox, dpiX, dpiY));
    }

    public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
    {
        ShowErrorCount++;
        ShowErrorCalls.Add((monitorId, anchorBox, dpiX, dpiY, shortMessage, operationId));
    }

    public void HideAll() => HideAllCount++;
}

public class FakeEscHook : IEscHook
{
    public event EventHandler? EscPressed;
    public int RaiseCount { get; private set; }

    public void RaiseEscPressed()
    {
        RaiseCount++;
        EscPressed?.Invoke(this, EventArgs.Empty);
    }
}

public static class CoordinatorTestHelpers
{
    public static WordInteractionCoordinator CreateCoordinator(
        out FakeAppLifecycle appLifecycle,
        out FakeHotkeyBroker broker,
        out FakeCursorService cursor,
        out FakeMonitorService monitors,
        out FakeScreenCapture capture,
        out FakeOcrEngine ocr,
        out FakeWordSelector selector,
        out FakeOverlayService overlay,
        out FakeTranslationRouter translator,
        out FakePopupService popup)
    {
        appLifecycle = new FakeAppLifecycle();
        broker = new FakeHotkeyBroker();
        cursor = new FakeCursorService();
        monitors = new FakeMonitorService();
        capture = new FakeScreenCapture();
        ocr = new FakeOcrEngine();
        selector = new FakeWordSelector();
        overlay = new FakeOverlayService();
        translator = new FakeTranslationRouter();
        popup = new FakePopupService();

        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
        var logger = NullLogger<WordInteractionCoordinator>.Instance;

        return new WordInteractionCoordinator(
            appLifecycle, broker, cursor, monitors, capture, ocr, selector, overlay, translator, popup, settings, logger);
    }

    public static async Task RunWordPipelineUntil(
        WordInteractionCoordinator coord,
        FakeHotkeyBroker broker,
        FakeScreenCapture capture,
        FakeOcrEngine? ocr,
        FakeTranslationRouter? translator,
        AppState targetState)
    {
        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(10);

        if (targetState >= AppState.Capturing)
        {
            capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
                new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
            await Task.Delay(10);
        }

        if (ocr != null && targetState >= AppState.Ocr)
        {
            ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(
                new PhysicalRect(0, 0, 720, 320)));
            await Task.Delay(10);
        }

        if (translator != null && targetState >= AppState.Translating)
        {
            translator.TranslateWordTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
            await Task.Delay(10);
        }
    }

    public static async Task CompleteAll(
        FakeScreenCapture capture,
        FakeOcrEngine ocr,
        FakeTranslationRouter translator,
        AppSettings settings)
    {
        if (!capture.CaptureAroundTcs.Task.IsCompleted)
            capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
                new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(5);

        if (!ocr.RecognizeTcs.Task.IsCompleted)
            ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(
                new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(5);

        if (!translator.TranslateWordTcs.Task.IsCompleted)
            translator.TranslateWordTcs.SetResult(FakeTranslationRouter.CreateResult("hello", settings.TargetLanguage));
        await Task.Delay(5);
    }

    public static async Task WaitForState(WordInteractionCoordinator coord, AppState target, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (coord.State != target && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
