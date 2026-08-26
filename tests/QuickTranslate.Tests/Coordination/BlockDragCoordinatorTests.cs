using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class BlockDragCoordinatorTests
{
    private static OcrLine MakeLine(int x, int y, int w, int h, string text)
    {
        return new OcrLine(new PhysicalRect(x, y, w, h), Array.Empty<OcrWord>(), text);
    }

    private static IReadOnlyList<OcrLine> Create5Lines()
    {
        // y 100,120,140,160,180 h20
        return new List<OcrLine>
        {
            MakeLine(10, 100, 200, 20, "line0"),
            MakeLine(10, 120, 200, 20, "line1"),
            MakeLine(10, 140, 200, 20, "line2"),
            MakeLine(10, 160, 200, 20, "line3"),
            MakeLine(10, 180, 200, 20, "line4"),
        };
    }

    private static PhysicalRect UnionOf(IEnumerable<OcrLine> lines)
    {
        int minX = lines.Min(l => l.Box.X);
        int minY = lines.Min(l => l.Box.Y);
        int maxR = lines.Max(l => l.Box.Right);
        int maxB = lines.Max(l => l.Box.Bottom);
        return new PhysicalRect(minX, minY, maxR - minX, maxB - minY);
    }

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
        out FakeHotkeyBroker broker)
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

        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
        var logger = NullLogger<BlockInteractionCoordinator>.Instance;
        var retry = new QuickTranslate.Infrastructure.Coordination.BlockRetryCoordinator(
            capture, ocr, selector, monitors, settings);

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
            hotkeyBroker: broker);
    }

    [Fact]
    public void Hold_DragDown_ExpandsUnionBox()
    {
        var lines = Create5Lines();
        // 16ms poll cursor Y to expand UnionBox among already OCRed lines
        // anchor 110 (line0), drag to 165 covers lines 0-3
        var expanded = BlockInteractionCoordinator.ExpandSelectedLines(lines, anchorY: 110, dragY: 165);
        var expected = UnionOf(lines.Take(4));
        Assert.Equal(expected, expanded);
    }

    [Fact]
    public void Hold_DragSmall_NoExpand()
    {
        var lines = Create5Lines();
        var initial = lines[0].Box;
        // drag 5px (110 -> 115) should stay initial line0
        var expanded = BlockInteractionCoordinator.ExpandSelectedLines(lines, anchorY: 110, dragY: 115);
        Assert.Equal(initial, expanded);
    }

    [Fact]
    public void Drag_OnlyDownward_UpwardIgnored()
    {
        var lines = Create5Lines();
        var initial = lines[0].Box;
        // drag Y < anchor => no change (only downward allowed)
        var expanded = BlockInteractionCoordinator.ExpandSelectedLines(lines, anchorY: 110, dragY: 90);
        Assert.Equal(initial, expanded);

        // Also verify coordinator does not update overlay when dragging upward
        var coord = CreateCoordinator(out var cursor, out var monitors, out var capture, out var ocr, out var selector, out var overlay, out var popup, out var translator, out var escHook, out var broker);
        var ocrResult = new OcrLayoutResult(new PhysicalRect(0, 0, 1200, 720), lines, new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero), DateTimeOffset.Now, 96, 96, "Fake");
        // inject drag state: anchor line0, initial union = line0
        coord.SetDragStateForTest(ocrResult, new PhysicalPoint(50, 110), lines[0].Box, new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"), 96, 96);
        coord.HandleHoldStartForTest(new PhysicalPoint(50, 110));
        cursor.CursorPos = new PhysicalPoint(50, 90); // upward
        int showBefore = overlay.ShowTotalCount;
        coord.TriggerDragTickForTest();
        // should not have expanded
        Assert.Equal(showBefore, overlay.ShowTotalCount);
        // drag downward should expand
        cursor.CursorPos = new PhysicalPoint(50, 165);
        coord.TriggerDragTickForTest();
        Assert.True(overlay.ShowTotalCount > showBefore);
        var lastBox = overlay.ShowCalls.Last().Box;
        var expected = UnionOf(lines.Take(4));
        Assert.Equal(expected, lastBox);
    }
}
