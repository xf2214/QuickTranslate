using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class CancelRaceTests
{
    [Fact]
    public async Task Case1_NewHotkeyDuringOcr_SuppressesOldOverlay()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup);

        var firstCaptureDone = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = firstCaptureDone;
        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);

        firstCaptureDone.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(30);

        Assert.Equal(AppState.Ocr, coord.State);
        var firstOcrTcs = ocr.RecognizeTcs;
        var firstSlot = coord.CurrentSlot;
        Assert.NotNull(firstSlot);

        var secondCaptureDone = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = secondCaptureDone;
        var secondOcrTcs = new TaskCompletionSource<OcrLayoutResult>();
        ocr.RecognizeTcs = secondOcrTcs;
        var secondTransTcs = new TaskCompletionSource<TranslationResult>();
        translator.TranslateWordTcs = secondTransTcs;

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);

        Assert.True(firstSlot!.Cts.IsCancellationRequested);

        secondCaptureDone.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(30);

        firstOcrTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(50);

        Assert.Equal(0, overlay.ShowTotalCount);

        secondOcrTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);

        Assert.Equal(AppState.Translating, coord.State);
        Assert.Equal(1, overlay.ShowTotalCount);

        secondTransTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(50);

        Assert.Equal(AppState.Displaying, coord.State);
        Assert.Equal(1, overlay.ShowTotalCount);
        Assert.Equal(1, popup.ShowCount);
    }

    [Fact]
    public async Task Case2_NewHotkeyDuringTranslating_SuppressesOldPopup()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup);

        var firstCaptureDone = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = firstCaptureDone;
        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(20);

        firstCaptureDone.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(20);

        var firstOcrDone = ocr.RecognizeTcs;
        firstOcrDone.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);

        Assert.Equal(AppState.Translating, coord.State);
        Assert.Equal(1, overlay.ShowTotalCount);
        var firstTransTcs = translator.TranslateWordTcs;
        var firstSlot = coord.CurrentSlot;
        Assert.NotNull(firstSlot);

        var secondCaptureDone = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = secondCaptureDone;
        var secondOcrDone = new TaskCompletionSource<OcrLayoutResult>();
        ocr.RecognizeTcs = secondOcrDone;
        var secondTransDone = new TaskCompletionSource<TranslationResult>();
        translator.TranslateWordTcs = secondTransDone;

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);

        Assert.True(firstSlot!.Cts.IsCancellationRequested);

        secondCaptureDone.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(30);

        firstTransTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(50);

        Assert.Equal(0, popup.ShowCount);

        secondOcrDone.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);

        Assert.Equal(AppState.Translating, coord.State);
        Assert.Equal(2, overlay.ShowTotalCount);

        secondTransDone.SetResult(FakeTranslationRouter.CreateResult("hello2", "zh-CN"));
        await Task.Delay(50);

        Assert.Equal(AppState.Displaying, coord.State);
        Assert.Equal(1, popup.ShowCount);
    }
}
