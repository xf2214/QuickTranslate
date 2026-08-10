using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Tests.Infrastructure;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class CoordinatorErrorTests
{
    private static void SetCurrentSlot(WordInteractionCoordinator coord, WordInteractionCoordinator.OperationSlot? slot)
    {
        var currentField = typeof(WordInteractionCoordinator).GetField("_current",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(coord, slot);
    }

    private static WordInteractionCoordinator CreateCoordinatorWithSpyLogger(
        out FakeAppLifecycle appLifecycle,
        out FakeHotkeyBroker broker,
        out FakeCursorService cursor,
        out FakeMonitorService monitors,
        out FakeScreenCapture capture,
        out FakeOcrEngine ocr,
        out FakeWordSelector selector,
        out FakeOverlayService overlay,
        out FakeTranslationRouter translator,
        out FakePopupService popup,
        out List<SpyLogEntry> logEntries)
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
        logEntries = new List<SpyLogEntry>();

        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
        var logger = new SpyLogger<WordInteractionCoordinator>(logEntries);

        return new WordInteractionCoordinator(
            appLifecycle, broker, cursor, monitors, capture, ocr, selector, overlay, translator, popup, settings, logger);
    }

    [Fact]
    public async Task Coordinator_TranslationException_Caught_PopupShowsShortError_NoTopLevelThrow()
    {
        var coord = CreateCoordinatorWithSpyLogger(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup,
            out var logEntries);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(20);

        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(20);

        ocr.RecognizeTcs.SetResult(FakeOcrEngine.CreateResult(
            new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(20);

        translator.TranslateWordTcs.SetException(TranslationException.AuthFailed());
        await Task.Delay(100);

        Assert.Equal(0, popup.ShowCount);
        Assert.Equal(1, popup.ShowErrorCount);
        Assert.Contains("授权失败", popup.ShowErrorCalls[0].Msg);

        Assert.Contains(logEntries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logEntries, e => e.Level == LogLevel.Error);

        Assert.True(overlay.HideAllCount >= 1);
        Assert.Equal(AppState.Idle, coord.State);
    }

    [Fact]
    public async Task Coordinator_OperationCanceled_SwallowedAsDebug()
    {
        var coord = CreateCoordinatorWithSpyLogger(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup,
            out var logEntries);

        var ocrTcs = new TaskCompletionSource<OcrLayoutResult>();
        ocr.RecognizeTcs = ocrTcs;

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(20);

        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(20);

        var currentSlot = coord.CurrentSlot;
        Assert.NotNull(currentSlot);

        ocrTcs.SetException(new OperationCanceledException("User canceled", currentSlot!.Cts.Token));
        await Task.Delay(100);

        Assert.Equal(0, popup.ShowCount);
        Assert.Equal(0, popup.ShowErrorCount);
        Assert.True(popup.HideAllCount >= 1);
        Assert.True(overlay.HideAllCount >= 1);

        Assert.Contains(logEntries, e =>
            e.Level == LogLevel.Debug || e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logEntries, e => e.Level == LogLevel.Error);

        Assert.Equal(AppState.Idle, coord.State);
    }

    [Fact]
    public async Task Coordinator_LateResponse_Dropped_WhenSlotObsolete()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup);

        var firstCaptureTcs = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = firstCaptureTcs;
        var firstOcrTcs = new TaskCompletionSource<OcrLayoutResult>();
        ocr.RecognizeTcs = firstOcrTcs;
        var firstTransTcs = new TaskCompletionSource<TranslationResult>();
        translator.TranslateWordTcs = firstTransTcs;

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(40);

        firstCaptureTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(40);

        firstOcrTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);

        var staleSlot = coord.CurrentSlot;
        Assert.NotNull(staleSlot);

        var secondCaptureTcs = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = secondCaptureTcs;
        var secondOcrTcs = new TaskCompletionSource<OcrLayoutResult>();
        ocr.RecognizeTcs = secondOcrTcs;
        var secondTransTcs = new TaskCompletionSource<TranslationResult>();
        translator.TranslateWordTcs = secondTransTcs;

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(40);

        Assert.NotSame(staleSlot, coord.CurrentSlot);

        firstTransTcs.SetResult(FakeTranslationRouter.CreateResult("first-stale", "zh-CN"));
        await Task.Delay(80);

        Assert.Equal(0, popup.ShowCount);

        secondCaptureTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(40);

        secondOcrTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(40);

        secondTransTcs.SetResult(FakeTranslationRouter.CreateResult("second-fresh", "zh-CN"));
        await Task.Delay(100);

        Assert.Equal(1, popup.ShowCount);
        staleSlot?.Cts.Dispose();
    }
}
