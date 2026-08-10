using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Coordination;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class BlockInteractionCoordinatorTests
{
    private static void ForceSetState(BlockInteractionCoordinator coord, AppState state, out BlockInteractionCoordinator.OperationSlot slot)
    {
        slot = new BlockInteractionCoordinator.OperationSlot(Guid.NewGuid(), new CancellationTokenSource(), state);

        var currentField = typeof(BlockInteractionCoordinator).GetField("_current",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(coord, slot);

        var stateProp = typeof(BlockInteractionCoordinator).GetProperty("State",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(stateProp);
        var stateSetMethod = stateProp!.GetSetMethod(true);
        Assert.NotNull(stateSetMethod);
        stateSetMethod!.Invoke(coord, new object[] { state });
    }

    private static BlockInteractionCoordinator CreateBlockCoordinator(
        out FakeCursorService cursor,
        out FakeMonitorService monitors,
        out FakeScreenCapture capture,
        out FakeOcrEngine ocr,
        out FakeBlockSelector blockSelector,
        out FakeOverlayService overlay,
        out FakeBlockPopupService popup,
        out FakeTranslationRouter translator,
        out FakeEscHook escHook)
    {
        cursor = new FakeCursorService();
        monitors = new FakeMonitorService();
        capture = new FakeScreenCapture();
        ocr = new FakeOcrEngine();
        blockSelector = new FakeBlockSelector();
        overlay = new FakeOverlayService();
        popup = new FakeBlockPopupService();
        translator = new FakeTranslationRouter();
        escHook = new FakeEscHook();

        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
        var logger = NullLogger<BlockInteractionCoordinator>.Instance;

        var retry = new BlockRetryCoordinator(
            capture: capture,
            ocr: ocr,
            selector: blockSelector,
            monitors: monitors,
            settings: settings);

        return new BlockInteractionCoordinator(
            cursorService: cursor,
            monitorService: monitors,
            retryCoordinator: retry,
            overlayService: overlay,
            popupService: popup,
            translationRouter: translator,
            settings: settings,
            logger: logger,
            escHook: escHook);
    }

    [Fact]
    public async Task Setup_FakeBlockPipeline_RunsCorrect()
    {
        var coord = CreateBlockCoordinator(
            out var cursor, out var monitors, out var capture,
            out var ocr, out var blockSelector, out var overlay,
            out var popup, out var translator, out _);

        coord.Start();

        var stateHistory = new List<AppState>();
        var ctsCaptured = new CancellationTokenSource();

        blockSelector.SelectFunc = (o, p, opts) =>
        {
            return new BlockSelectionResult(
                BlockText: "Hello block text",
                UnionBox: new PhysicalRect(100, 100, 300, 80),
                SelectedLines: new[] { new OcrLine(new PhysicalRect(100, 100, 200, 30), Array.Empty<OcrWord>(), "Hello", 0) },
                Kind: SelectionKind.Block,
                OperationId: Guid.NewGuid(),
                NoBlockFound: false);
        };

        var runTask = Task.Run(() => coord.RunBlockPipeline());
        await Task.Delay(20);

        var frame = FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 1200, 720), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"));
        capture.CaptureAroundTcs.SetResult(frame);
        await Task.Delay(20);

        var ocrResult = FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 1200, 720));
        ocr.RecognizeTcs.SetResult(ocrResult);
        await Task.Delay(40);

        var transResult = FakeTranslationRouter.CreateResult("Hello block text", "zh-CN");
        translator.TranslateBlockTcs.SetResult(transResult);
        await Task.Delay(40);

        Assert.Equal(1, overlay.ShowTotalCount);
        Assert.Equal(1, popup.ShowCount);
    }

    [Fact]
    public async Task EscPress_InDisplaying_ClearsOverlayAndPopup()
    {
        var coord = CreateBlockCoordinator(
            out _, out _, out _, out _, out _,
            out var overlay, out var popup, out _, out var escHook);

        coord.Start();

        ForceSetState(coord, AppState.Displaying, out var slot);

        Assert.Equal(AppState.Displaying, coord.State);
        Assert.False(slot.Cts.IsCancellationRequested);

        escHook.RaiseEscPressed();
        await Task.Delay(50);

        Assert.Equal(AppState.Idle, coord.State);
        Assert.True(slot.Cts.IsCancellationRequested);
        Assert.True(overlay.HideAllCount >= 1);
        Assert.True(popup.HideAllCount >= 1);

        slot.Cts.Dispose();
    }

    [Fact]
    public async Task NewHotkey_CancelsPrevious_InOcrStage()
    {
        var coord = CreateBlockCoordinator(
            out _, out _, out var capture, out var ocr, out _,
            out _, out _, out var translator, out _);

        coord.Start();

        var firstRun = Task.Run(() => coord.RunBlockPipeline());
        await Task.Delay(20);

        var frame1 = FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 1200, 720), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"));
        capture.CaptureAroundTcs.SetResult(frame1);
        await Task.Delay(20);

        var slot1 = coord.CurrentSlot;
        Assert.NotNull(slot1);

        var capture2 = new FakeScreenCapture();
        var ocr2 = new FakeOcrEngine();
        var blockSelector2 = new FakeBlockSelector();
        var overlay2 = new FakeOverlayService();
        var popup2 = new FakeBlockPopupService();
        var translator2 = new FakeTranslationRouter();
        var escHook2 = new FakeEscHook();
        var cursor2 = new FakeCursorService();
        var monitors2 = new FakeMonitorService();
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
        var logger = NullLogger<BlockInteractionCoordinator>.Instance;
        var retry2 = new BlockRetryCoordinator(capture2, ocr2, blockSelector2, monitors2, settings);

        var coord2 = new BlockInteractionCoordinator(
            cursor2, monitors2, retry2, overlay2, popup2, translator2, settings, logger, escHook2);
        coord2.Start();

        var run1 = Task.Run(() => coord2.RunBlockPipeline());
        await Task.Delay(15);
        capture2.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 1200, 720), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        await Task.Delay(15);
        var firstSlot = coord2.CurrentSlot;
        Assert.NotNull(firstSlot);

        var run2 = Task.Run(() => coord2.RunBlockPipeline());
        await Task.Delay(20);

        Assert.True(firstSlot!.Cts.IsCancellationRequested,
            "First operation CT should be cancelled by second hotkey");

        Assert.NotSame(firstSlot, coord2.CurrentSlot);
    }
}
