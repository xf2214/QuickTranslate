using System.Reflection;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class OperationIdStaleDiscardTests
{
    private static void SetCurrentSlot(WordInteractionCoordinator coord, WordInteractionCoordinator.OperationSlot? slot)
    {
        var currentField = typeof(WordInteractionCoordinator).GetField("_current",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(coord, slot);
    }

    [Fact]
    public async Task StaleSlot_BeforeOverlayShow_DoesNotCallOverlay()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup);

        var captureTcs = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = captureTcs;
        var ocrTcs = new TaskCompletionSource<OcrLayoutResult>();
        ocr.RecognizeTcs = ocrTcs;

        var fireAndForget = Task.Run(() => coord.HandleHotkey(new HotkeyEvent(HotkeyEventType.Word, DateTimeOffset.Now)));
        await Task.Delay(40);

        captureTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(40);

        var staleSlot = coord.CurrentSlot;
        Assert.NotNull(staleSlot);

        var replacementSlot = new WordInteractionCoordinator.OperationSlot(
            Guid.NewGuid(), new CancellationTokenSource(), AppState.Capturing);
        SetCurrentSlot(coord, replacementSlot);

        ocrTcs.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(100);

        Assert.Equal(0, overlay.SelectionShowCount);
        Assert.Equal(0, popup.ShowCount);
        Assert.Same(replacementSlot, coord.CurrentSlot);
        Assert.NotSame(staleSlot, coord.CurrentSlot);

        replacementSlot.Cts.Dispose();
        staleSlot?.Cts.Dispose();
    }

    [Fact]
    public async Task StaleSlot_BeforeTranslate_DoesNotCallPopup()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup);

        var captureDone = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = captureDone;
        var fireAndForget = Task.Run(() => coord.HandleHotkey(new HotkeyEvent(HotkeyEventType.Word, DateTimeOffset.Now)));
        await Task.Delay(20);

        captureDone.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(20);

        var ocrDone = ocr.RecognizeTcs;
        ocrDone.SetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 720, 320)));
        await Task.Delay(80);

        Assert.True(coord.State == AppState.Translating || coord.State == AppState.OverlayVisible);
        Assert.Equal(1, overlay.SelectionShowCount);

        var staleSlot = coord.CurrentSlot;
        Assert.NotNull(staleSlot);

        var replacementSlot = new WordInteractionCoordinator.OperationSlot(
            Guid.NewGuid(), new CancellationTokenSource(), AppState.Capturing);
        SetCurrentSlot(coord, replacementSlot);

        var transTcs = new TaskCompletionSource<TranslationResult>();
        translator.TranslateWordTcs = transTcs;
        transTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(80);

        Assert.Equal(0, popup.ShowCount);

        replacementSlot.Cts.Dispose();
    }
}
