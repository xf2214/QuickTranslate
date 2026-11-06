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

public class BlockDragSelectionCoordinatorTests
{
    private static BlockInteractionCoordinator CreateCoordinator(
        out FakeCursorService cursor,
        out FakeMonitorService monitors,
        out FakeScreenCapture capture,
        out FakeOcrEngine ocr,
        out FakeBlockSelector selector,
        out FakeOverlayService overlay,
        out FakeBlockPopupService popup,
        out FakeTranslationRouter translator,
        out FakeEscHook escHook,
        out FakeHotkeyBroker broker,
        bool enableDragSelection = true)
    {
        cursor = new FakeCursorService();
        monitors = new FakeMonitorService();
        capture = new FakeScreenCapture();
        ocr = new FakeOcrEngine();
        selector = new FakeBlockSelector();
        overlay = new FakeOverlayService();
        popup = new FakeBlockPopupService();
        translator = new FakeTranslationRouter();
        escHook = new FakeEscHook();
        broker = new FakeHotkeyBroker();

        var settings = Options.Create(new AppSettings
        {
            TargetLanguage = "zh-CN",
            EnableDragSelection = enableDragSelection
        });
        var logger = NullLogger<BlockInteractionCoordinator>.Instance;
        var retry = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);
        var statusIndicator = new FakeStatusIndicatorService();

        return new BlockInteractionCoordinator(
            cursorService: cursor,
            monitorService: monitors,
            retryCoordinator: retry,
            overlayService: overlay,
            popupService: popup,
            translationRouter: translator,
            settings: settings,
            logger: logger,
            escHook: escHook,
            statusIndicator: statusIndicator,
            hotkeyBroker: broker);
    }

    [Fact]
    public void RunBlockPipeline_WhenDragSelectionEnabled_IsBlocked()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        coord.RunBlockPipeline();

        Assert.Equal(AppState.Idle, coord.State);
        Assert.Equal(0, capture.CaptureAroundCount);
    }

    [Fact]
    public void DragSelection_SmallMovementUnder5px_OverlayRemainsHidden()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        var anchor = new PhysicalPoint(100, 100);
        cursor.CursorPos = anchor;
        coord.HandleHoldStartForTest(anchor);

        cursor.CursorPos = new PhysicalPoint(102, 103); // w=2, h=3 < 5
        coord.TriggerDragTickForTest();

        Assert.Equal(0, overlay.ShowTotalCount);
        Assert.Equal(0, overlay.UpdateCount);
    }

    [Fact]
    public void DragSelection_MouseMovement_UpdatesFreeformRectangle()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        var anchor = new PhysicalPoint(100, 100);
        cursor.CursorPos = anchor;
        coord.HandleHoldStartForTest(anchor);

        cursor.CursorPos = new PhysicalPoint(250, 180); // w=150, h=80
        coord.TriggerDragTickForTest();

        Assert.True(overlay.ShowTotalCount >= 1);
        var lastBox = overlay.UpdateCalls.Last().Box;
        Assert.Equal(new PhysicalRect(100, 100, 150, 80), lastBox);
    }

    [Fact]
    public void DragSelection_OppositeDirectionDrag_ComputesCorrectRectangle()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        var anchor = new PhysicalPoint(250, 180);
        cursor.CursorPos = anchor;
        coord.HandleHoldStartForTest(anchor);

        cursor.CursorPos = new PhysicalPoint(100, 100); // drag top-left
        coord.TriggerDragTickForTest();

        Assert.True(overlay.ShowTotalCount >= 1);
        var lastBox = overlay.UpdateCalls.Last().Box;
        Assert.Equal(new PhysicalRect(100, 100, 150, 80), lastBox);
    }

    [Fact]
    public void DragSelection_ShortTapUnder15px_HandleHoldEnd_IgnoresAndReturnsToIdle()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        var anchor = new PhysicalPoint(100, 100);
        cursor.CursorPos = anchor;
        coord.HandleHoldStartForTest(anchor);

        cursor.CursorPos = new PhysicalPoint(108, 106); // w=8, h=6 < 15
        coord.HandleHoldEndForTest();

        Assert.Equal(AppState.Idle, coord.State);
        Assert.Equal(0, capture.CaptureAroundCount);
        Assert.Empty(popup.ShowCalls);
    }

    [Fact]
    public async Task DragSelection_ValidSize_CapturesRectAndTranslates()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        var lines = new List<OcrLine>
        {
            new(new PhysicalRect(110, 110, 80, 20), Array.Empty<OcrWord>(), "Hello world")
        };
        ocr.QueuedResults.Enqueue(new OcrLayoutResult(
            new PhysicalRect(100, 100, 200, 100),
            lines,
            new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            DateTimeOffset.Now, 96, 96, "Fake"));

        translator.TranslateBlockTcs.SetResult(new TranslationResult(
            "hello world",
            "Hello world",
            "你好世界",
            "zh-CN",
            FromCache: true,
            FromDictionary: false));

        var anchor = new PhysicalPoint(100, 100);
        cursor.CursorPos = anchor;
        coord.HandleHoldStartForTest(anchor);

        cursor.CursorPos = new PhysicalPoint(300, 200); // w=200, h=100 >= 15
        coord.HandleHoldEndForTest();

        // Wait for background Task.Run to complete OCR and translation
        for (int i = 0; i < 20 && popup.ShowCalls.Count == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Single(popup.ShowCalls);
        var show = popup.ShowCalls[0];
        Assert.Equal(new PhysicalRect(100, 100, 200, 100), show.Box);
        Assert.Equal("Hello world", show.Block.BlockText);
    }

    [Fact]
    public void DragSelection_EscPressed_CancelsAndCleansUp()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        var anchor = new PhysicalPoint(100, 100);
        cursor.CursorPos = anchor;
        coord.HandleHoldStartForTest(anchor);

        cursor.CursorPos = new PhysicalPoint(300, 200);
        coord.TriggerDragTickForTest();
        Assert.True(overlay.ShowTotalCount >= 1);

        escHook.RaiseEscPressed();

        Assert.Equal(AppState.Idle, coord.State);
        Assert.True(overlay.HideAllCount > 0);
    }

    [Fact]
    public async Task DragSelection_NoTextDetected_ShowsIndicatorAndReturnsToIdle()
    {
        var coord = CreateCoordinator(
            out var cursor, out var monitors, out var capture, out var ocr,
            out var selector, out var overlay, out var popup, out var translator,
            out var escHook, out var broker, enableDragSelection: true);

        ocr.QueuedResults.Enqueue(new OcrLayoutResult(
            new PhysicalRect(100, 100, 200, 100),
            Array.Empty<OcrLine>(),
            new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            DateTimeOffset.Now, 96, 96, "Fake"));

        var anchor = new PhysicalPoint(100, 100);
        cursor.CursorPos = anchor;
        coord.HandleHoldStartForTest(anchor);

        cursor.CursorPos = new PhysicalPoint(300, 200);
        coord.HandleHoldEndForTest();

        // Wait for OCR pipeline
        await Task.Delay(200);

        Assert.Empty(popup.ShowCalls);
    }
}
