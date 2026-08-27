using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Coordination;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

internal class FakeSelectedTextProbe : ISelectedTextProbe
{
    public SelectedTextProbeResult? ResultToReturn { get; set; }
    public Func<PhysicalPoint, CancellationToken, Task<SelectedTextProbeResult?>>? ProbeFunc { get; set; }
    public int ProbeCount { get; private set; }
    public Exception? ExceptionToThrow { get; set; }

    public Task<SelectedTextProbeResult?> ProbeAsync(PhysicalPoint cursor, CancellationToken ct)
    {
        ProbeCount++;
        if (ExceptionToThrow != null) throw ExceptionToThrow;
        if (ProbeFunc != null) return ProbeFunc(cursor, ct);
        return Task.FromResult(ResultToReturn);
    }
}

internal class CapturingTranslationRouter : ITranslationRouter
{
    public int TranslateWordCount { get; private set; }
    public int TranslateBlockCount { get; private set; }
    public string? LastWordText { get; private set; }
    public string? LastBlockText { get; private set; }
    public TaskCompletionSource<TranslationResult> WordTcs { get; set; } = new();
    public TaskCompletionSource<TranslationResult> BlockTcs { get; set; } = new();

    public Task<TranslationResult> TranslateWordAsync(string word, string targetLang, CancellationToken ct = default)
    {
        TranslateWordCount++;
        LastWordText = word;
        return WordTcs.Task;
    }

    public Task<TranslationResult> TranslateBlockAsync(string blockText, string targetLang, CancellationToken ct = default)
    {
        TranslateBlockCount++;
        LastBlockText = blockText;
        return BlockTcs.Task;
    }
}

public class SelectedTextProbePipelineTests
{
    private static WordInteractionCoordinator CreateWordCoordinator(
        IOptions<AppSettings> settings,
        ISelectedTextProbe? probe,
        out FakeAppLifecycle lifecycle,
        out FakeHotkeyBroker broker,
        out FakeCursorService cursor,
        out FakeMonitorService monitors,
        out FakeScreenCapture capture,
        out FakeOcrEngine ocr,
        out FakeWordSelector selector,
        out FakeOverlayService overlay,
        out FakePopupService popup,
        out CapturingTranslationRouter translator)
    {
        lifecycle = new FakeAppLifecycle();
        broker = new FakeHotkeyBroker();
        cursor = new FakeCursorService();
        monitors = new FakeMonitorService();
        capture = new FakeScreenCapture();
        ocr = new FakeOcrEngine();
        selector = new FakeWordSelector();
        overlay = new FakeOverlayService();
        popup = new FakePopupService();
        translator = new CapturingTranslationRouter();
        var logger = NullLogger<WordInteractionCoordinator>.Instance;
        return new WordInteractionCoordinator(lifecycle, broker, cursor, monitors, capture, ocr, selector, overlay, translator, popup, settings, logger, null, probe);
    }

    private static BlockInteractionCoordinator CreateBlockCoordinator(
        IOptions<AppSettings> settings,
        ISelectedTextProbe? probe,
        out FakeCursorService cursor,
        out FakeMonitorService monitors,
        out FakeScreenCapture capture,
        out FakeOcrEngine ocr,
        out FakeBlockSelector selector,
        out FakeOverlayService overlay,
        out FakeBlockPopupService popup,
        out CapturingTranslationRouter translator,
        out FakeEscHook escHook,
        out BlockRetryCoordinator retry)
    {
        cursor = new FakeCursorService();
        monitors = new FakeMonitorService();
        capture = new FakeScreenCapture();
        ocr = new FakeOcrEngine();
        selector = new FakeBlockSelector();
        overlay = new FakeOverlayService();
        popup = new FakeBlockPopupService();
        translator = new CapturingTranslationRouter();
        escHook = new FakeEscHook();
        retry = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);
        var logger = NullLogger<BlockInteractionCoordinator>.Instance;
        return new BlockInteractionCoordinator(cursor, monitors, retry, overlay, popup, translator, settings, logger, escHook, null, probe);
    }

    [Fact]
    public async Task Word_ProbeHit_SkipsOcr_UsesNormalizedText_AndBoxEqualsUnion()
    {
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN", EnableSelectedTextProbe = true });
        // 选区必须覆盖默认光标位 (100,100)，否则空间校验会正确拒绝（见 FarFromCursor 用例）
        var union = new PhysicalRect(40, 70, 120, 60);
        var probe = new FakeSelectedTextProbe
        {
            ResultToReturn = new SelectedTextProbeResult("  hello probe \r\n", new[] { union }, union, SelectedTextSource.UiAutomation)
        };
        var coord = CreateWordCoordinator(settings, probe, out _, out var broker, out _, out _, out var capture, out var ocr, out _, out var overlay, out var popup, out var translator);

        translator.WordTcs.SetResult(new TranslationResult("hello probe", "hello probe", "译文", "zh-CN", false, false, false));

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(120);

        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(0, capture.CaptureAroundCount);
        Assert.Equal(0, ocr.RecognizeCount);
        Assert.Equal(1, translator.TranslateWordCount);
        Assert.Equal("hello probe", translator.LastWordText); // Normalize 后文本
        Assert.Equal(1, popup.ShowCount);
        var shownSel = popup.ShowCalls[0].Sel;
        Assert.Equal(union, shownSel.Box);
        // overlay 应显示选中框（非 preview）
        Assert.Equal(1, overlay.SelectionShowCount);
        Assert.Equal(union, overlay.ShowCalls.First(c => !c.Preview).Box);

        coord.CancelAll(true);
    }

    [Fact]
    public async Task Word_ProbeReturnsNull_FallsBackToOcr()
    {
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN", EnableSelectedTextProbe = true });
        var probe = new FakeSelectedTextProbe { ResultToReturn = null };
        var coord = CreateWordCoordinator(settings, probe, out _, out var broker, out _, out _, out var capture, out var ocr, out _, out _, out _, out var translator);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);
        Assert.Equal(AppState.Capturing, coord.State);

        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(30);
        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);
        // 进入 Translating 说明已走 OCR 路径
        Assert.Equal(AppState.Translating, coord.State);
        Assert.Equal(1, ocr.RecognizeCount);

        translator.WordTcs.SetResult(new TranslationResult("hello", "hello", "译文", "zh-CN", false, false, false));
        await Task.Delay(400);
        Assert.Equal(AppState.Displaying, coord.State);

        coord.CancelAll(true);
    }

    [Fact]
    public async Task Word_DisabledProbe_SkipsProbeAndUsesOcr()
    {
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN", EnableSelectedTextProbe = false });
        var union = new PhysicalRect(10, 20, 100, 30);
        var probe = new FakeSelectedTextProbe
        {
            ResultToReturn = new SelectedTextProbeResult("should be ignored", new[] { union }, union, SelectedTextSource.UiAutomation)
        };
        var coord = CreateWordCoordinator(settings, probe, out _, out var broker, out _, out _, out var capture, out var ocr, out _, out _, out _, out var translator);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(50);

        Assert.Equal(0, probe.ProbeCount);
        Assert.Equal(1, capture.CaptureAroundCount); // 已进入截屏，说明未走探测分支

        // 清理剩余流水线
        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(30);
        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);
        translator.WordTcs.SetResult(new TranslationResult("hello", "hello", "译文", "zh-CN", false, false, false));
        await Task.Delay(400);

        coord.CancelAll(true);
    }

    [Fact]
    public async Task Block_ProbeHit_SkipsOcr_TranslatesProbeText()
    {
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN", EnableSelectedTextProbe = true });
        var union = new PhysicalRect(50, 60, 300, 120);
        var probe = new FakeSelectedTextProbe
        {
            ResultToReturn = new SelectedTextProbeResult("block probe text\r\nsecond line", new[] { union }, union, SelectedTextSource.UiAutomation)
        };
        var coord = CreateBlockCoordinator(settings, probe, out _, out _, out var capture, out var ocr, out _, out var overlay, out var popup, out var translator, out _, out _);
        coord.Start();
        translator.BlockTcs.SetResult(new TranslationResult("block probe text\nsecond line", "block probe text\nsecond line", "块译文", "zh-CN", false, false, false));

        coord.RunBlockPipeline();
        await Task.Delay(120);

        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(0, capture.CaptureAroundCount);
        Assert.Equal(0, ocr.RecognizeCount);
        Assert.Equal(1, translator.TranslateBlockCount);
        Assert.Equal("block probe text\nsecond line", translator.LastBlockText);
        Assert.Equal(1, popup.ShowCount);
        Assert.Equal(union, popup.ShowCalls[0].Box);
        Assert.Equal(1, overlay.ShowTotalCount);

        coord.Dispose();
    }

    [Fact]
    public async Task Word_ProbeHit_FarFromCursor_FallsBackToOcr()
    {
        // 陈旧选区劫持回归：文档残留选区远离光标 → 必须拒绝探测结果，回退 OCR
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN", EnableSelectedTextProbe = true });
        var staleUnion = new PhysicalRect(3000, 1800, 200, 40);
        var probe = new FakeSelectedTextProbe
        {
            ResultToReturn = new SelectedTextProbeResult("stale selection text", new[] { staleUnion }, staleUnion, SelectedTextSource.UiAutomation)
        };
        var coord = CreateWordCoordinator(settings, probe, out _, out var broker, out _, out _, out var capture, out var ocr, out _, out _, out _, out var translator);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);
        // 探测被采纳则 Capture==0；空间校验生效时应已进入截屏
        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(1, capture.CaptureAroundCount);

        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(30);
        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);
        translator.WordTcs.SetResult(new TranslationResult("hello", "hello", "译文", "zh-CN", false, false, false));
        await Task.Delay(400);

        // 走了 OCR 链路：翻译的是选择器产出的词，绝非陈旧选区文本
        Assert.Equal(1, translator.TranslateWordCount);
        Assert.NotEqual("stale selection text", translator.LastWordText);
        Assert.Equal(AppState.Displaying, coord.State);

        coord.CancelAll(true);
    }

    [Fact]
    public async Task Block_ProbeHit_FarFromCursor_FallsBackToOcr()
    {
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN", EnableSelectedTextProbe = true });
        var staleUnion = new PhysicalRect(3000, 1800, 200, 40);
        var probe = new FakeSelectedTextProbe
        {
            ResultToReturn = new SelectedTextProbeResult("stale block selection", new[] { staleUnion }, staleUnion, SelectedTextSource.UiAutomation)
        };
        var coord = CreateBlockCoordinator(settings, probe, out _, out _, out var capture, out var ocr, out var selector, out _, out _, out var translator, out _, out _);
        coord.Start();
        selector.SelectFunc = (o, p, opts) => new BlockSelectionResult("fallback block", new PhysicalRect(100, 100, 300, 80), Array.Empty<QuickTranslate.Core.Ocr.OcrLine>(), SelectionKind.Block, Guid.NewGuid(), false);

        var run = Task.Run(() => coord.RunBlockPipeline());
        await Task.Delay(20);
        Assert.Equal(1, probe.ProbeCount);
        // 空间校验生效：不采纳远距选区，进入截屏
        Assert.Equal(1, capture.CaptureAroundCount);

        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 1200, 720), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(20);
        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 1200, 720)));
        await Task.Delay(40);
        translator.BlockTcs.SetResult(new TranslationResult("fallback block", "fallback block", "译文", "zh-CN", false, false, false));
        await Task.Delay(400);

        Assert.Equal("fallback block", translator.LastBlockText);

        coord.Dispose();
        await run;
    }

    [Fact]
    public async Task Block_ProbeReturnsNull_FallsBackToOcr()
    {
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN", EnableSelectedTextProbe = true });
        var probe = new FakeSelectedTextProbe { ResultToReturn = null };
        var coord = CreateBlockCoordinator(settings, probe, out _, out _, out var capture, out var ocr, out var selector, out _, out _, out var translator, out _, out _);
        coord.Start();
        selector.SelectFunc = (o, p, opts) => new BlockSelectionResult("fallback", new PhysicalRect(100, 100, 300, 80), new[] { new QuickTranslate.Core.Ocr.OcrLine(new PhysicalRect(100, 100, 200, 30), Array.Empty<QuickTranslate.Core.Ocr.OcrWord>(), "fallback") }, SelectionKind.Block, Guid.NewGuid(), false);

        var run = Task.Run(() => coord.RunBlockPipeline());
        await Task.Delay(20);
        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 1200, 720), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(20);
        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 1200, 720)));
        await Task.Delay(40);
        translator.BlockTcs.SetResult(new TranslationResult("fallback", "fallback", "译文", "zh-CN", false, false, false));
        await Task.Delay(400);

        Assert.True(capture.CaptureAroundCount >= 1);
        Assert.True(ocr.RecognizeCount >= 1);

        coord.Dispose();
        await run;
    }
}
