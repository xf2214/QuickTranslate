using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Tests.Coordination;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

/// <summary>
/// 验证协调器在耗时阶段驱动加载指示器：
/// 热键按下立即 Show("正在识别…")，翻译时 Update("正在翻译…")，
/// 结束（成功/失败/取消）必须 Hide——指示器绝不能遗留屏幕。
/// </summary>
public class StatusIndicatorLifecycleTests
{
    private static (WordInteractionCoordinator Coord, FakeHotkeyBroker Broker, FakeScreenCapture Capture,
        FakeOcrEngine Ocr, FakeTranslationRouter Translator, FakeStatusIndicatorService Indicator)
        CreateWord()
    {
        var broker = new FakeHotkeyBroker();
        var cursor = new FakeCursorService();
        var monitors = new FakeMonitorService();
        var capture = new FakeScreenCapture();
        var ocr = new FakeOcrEngine();
        var selector = new FakeWordSelector();
        var overlay = new FakeOverlayService();
        var translator = new FakeTranslationRouter();
        var popup = new FakePopupService();
        var indicator = new FakeStatusIndicatorService();

        var coord = new WordInteractionCoordinator(
            new FakeAppLifecycle(), broker, cursor, monitors, capture, ocr, selector,
            overlay, translator, popup,
            Options.Create(new AppSettings { TargetLanguage = "zh-CN" }),
            NullLogger<WordInteractionCoordinator>.Instance,
            indicator);

        return (coord, broker, capture, ocr, translator, indicator);
    }

    private static void CompleteHappyPath(FakeHotkeyBroker broker, FakeScreenCapture capture,
        FakeOcrEngine ocr, FakeTranslationRouter translator)
    {
        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        translator.TranslateWordTcs.SetResult(new TranslationResult(
            "k||zh", "hello", "你好", "zh-CN", false, false, false));
    }

    [Fact]
    public async Task WordPipeline_ShowsIndicator_OnHotkey_AndHides_OnSuccess()
    {
        var (coord, broker, capture, ocr, translator, indicator) = CreateWord();
        Assert.NotNull(coord);

        CompleteHappyPath(broker, capture, ocr, translator);
        await Task.Delay(400);

        Assert.Equal(1, indicator.ShowCount);
        Assert.Contains("正在识别…", indicator.ShowMessages);
        Assert.Contains("Update:正在翻译…", indicator.Messages);
        Assert.Equal(1, indicator.HideCount);
    }

    [Fact]
    public async Task WordPipeline_HidesIndicator_OnTranslationError()
    {
        var (coord, broker, capture, ocr, translator, indicator) = CreateWord();
        Assert.NotNull(coord);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        translator.TranslateWordTcs.SetException(TranslationException.Timeout());
        await Task.Delay(400);

        Assert.Equal(1, indicator.ShowCount);
        Assert.True(indicator.HideCount >= 1);
    }

    [Fact]
    public async Task CancelAll_HidesIndicator()
    {
        var (coord, broker, capture, ocr, translator, indicator) = CreateWord();

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30); // 进入 Capturing/OCR 阶段（指示器已显示）

        coord.CancelAll(returnIdle: true);
        await Task.Delay(100);

        Assert.Equal(1, indicator.ShowCount);
        Assert.True(indicator.HideCount >= 1);
    }
}
