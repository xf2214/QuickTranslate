using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Infrastructure.Coordination;
using Xunit;
using System.Drawing;
using System.Drawing.Imaging;

namespace QuickTranslate.Tests.Coordination;

public class BlockDragOverlayTests
{
    private static OcrLine MakeLine(int x, int y, int w, int h, string text)
        => new(new PhysicalRect(x, y, w, h), Array.Empty<OcrWord>(), text);

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
        var retry = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);
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

    private static PhysicalRect UnionOf(IEnumerable<OcrLine> lines)
    {
        int minX = lines.Min(l => l.Box.X);
        int minY = lines.Min(l => l.Box.Y);
        int maxR = lines.Max(l => l.Box.Right);
        int maxB = lines.Max(l => l.Box.Bottom);
        return new PhysicalRect(minX, minY, maxR - minX, maxB - minY);
    }

    private static ScreenFrame FrameFor(PhysicalRect region)
    {
        var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), PixelFormat.Format32bppArgb);
        return new ScreenFrame(bmp, region, new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"));
    }

    [Fact]
    public void Drag_ShowsPreview_WithoutScan_ThenHoldEndShowsScan()
    {
        var lines = new List<OcrLine>
        {
            MakeLine(10,100,200,20,"line0"),
            MakeLine(10,120,200,20,"line1"),
            MakeLine(10,140,200,20,"line2"),
            MakeLine(10,160,200,20,"line3"),
            MakeLine(10,180,200,20,"line4"),
        };
        var ocrResult = new OcrLayoutResult(new PhysicalRect(0,0,1200,720), lines, new OcrTimings(TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero), DateTimeOffset.Now, 96,96,"Fake");
        var coord = CreateCoordinator(out var cursor, out var monitors, out var capture, out var ocr, out var selector, out var overlay, out var popup, out var translator, out var escHook, out var broker);

        var frame = FrameFor(new PhysicalRect(0,0,1200,720));
        coord.SetDragStateForTest(ocrResult, new PhysicalPoint(50,110), lines[0].Box, new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"), 96,96);
        coord.SetDragFrameForTest(frame);
        coord.HandleHoldStartForTest(new PhysicalPoint(50,110));

        // During hold start preview is true
        Assert.True(overlay.ShowCalls.Last().Preview);

        // Drag down to 165 should expand via Update (preview mode), not Show
        cursor.CursorPos = new PhysicalPoint(50,165);
        int showCountBefore = overlay.ShowTotalCount;
        int updateBefore = overlay.UpdateCount;
        coord.TriggerDragTickForTest();

        // Task3 expects: drag tick uses Update keeping preview, not Show preview:true each tick
        Assert.Equal(showCountBefore, overlay.ShowTotalCount);
        Assert.True(overlay.UpdateCount > updateBefore);
        var expected = UnionOf(lines.Take(4));
        Assert.Equal(expected, overlay.UpdateCalls.Last().Box);

        // HoldEnd should Show with preview:false (scan)
        coord.HandleHoldEndForTest();
        var last = overlay.ShowCalls.Last();
        Assert.False(last.Preview);
        Assert.Equal(expected, last.Box);
    }

    [Fact]
    public void DragBeyondFrame_ExpandsCaptureTo1600()
    {
        // initial frame 1200x720 centered at anchor (200,200): region 0..1200 x -160..560? Simplify: region (0,0,1200,720) bottom=720
        var initialLines = new List<OcrLine>
        {
            MakeLine(10,100,200,20,"line0"),
            MakeLine(10,120,200,20,"line1"),
            MakeLine(10,140,200,20,"line2"),
        };
        var expandedLines = new List<OcrLine>(initialLines)
        {
            MakeLine(10,700,200,20,"line_far"),
            MakeLine(10,750,200,20,"line_new"),
        };
        var initialOcr = new OcrLayoutResult(new PhysicalRect(0,0,1200,720), initialLines, new OcrTimings(TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero), DateTimeOffset.Now, 96,96,"Fake");
        var expandedOcr = new OcrLayoutResult(new PhysicalRect(0,0,1600,1200), expandedLines, new OcrTimings(TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero,TimeSpan.Zero), DateTimeOffset.Now, 96,96,"Fake");

        var coord = CreateCoordinator(out var cursor, out var monitors, out var capture, out var ocr, out var selector, out var overlay, out var popup, out var translator, out var escHook, out var broker);

        var initialFrame = FrameFor(new PhysicalRect(0,0,1200,720));
        coord.SetDragStateForTest(initialOcr, new PhysicalPoint(50,110), initialLines[0].Box, new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"), 96,96);
        coord.SetDragFrameForTest(initialFrame);

        // Queue expanded OCR for recognition after capture
        ocr.QueuedResults.Enqueue(expandedOcr);
        // Capture mock will return 1600x1200 frame via func
        capture.CaptureAroundFunc = (anchor, size) =>
        {
            // verify requested size is 1600x1200
            return FrameFor(new PhysicalRect(0,0,size.Width,size.Height));
        };

        coord.HandleHoldStartForTest(new PhysicalPoint(50,110));

        // dragY 800 exceeds initial frame bottom 720-20=700 threshold -> triggers expansion
        cursor.CursorPos = new PhysicalPoint(50, 800);
        coord.TriggerDragTickForTest();

        Assert.True(capture.CaptureAroundSizes.Count >= 1);
        var lastSize = capture.CaptureAroundSizes.Last();
        Assert.Equal(1600, lastSize.Width);
        Assert.Equal(1200, lastSize.Height);
        // new lines included
        Assert.Contains(overlay.UpdateCalls, u => u.Box.Bottom >= 770);
    }
}
