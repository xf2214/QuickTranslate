using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Coordination;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class BlockDragAccuracyRegressionTests
{
    private static readonly MonitorId Monitor = new(new IntPtr(1), @"\\.\DISPLAY1");

    private static OcrLine Line(int x, int y, string text)
        => new(new PhysicalRect(x, y, 200, 20), Array.Empty<OcrWord>(), text);

    private static OcrLayoutResult MultiColumnOcr()
    {
        // Deliberately interleave the right column first: a Y-only anchor lookup
        // or range filter will select unrelated text and enlarge the overlay.
        OcrLine[] lines =
        {
            Line(500, 100, "R0"), Line(10, 100, "L0"),
            Line(500, 120, "R1"), Line(10, 120, "L1"),
            Line(500, 140, "R2"), Line(10, 140, "L2"),
            Line(500, 160, "R3"), Line(10, 160, "L3"),
        };
        return new OcrLayoutResult(
            new PhysicalRect(0, 0, 1200, 720), lines,
            new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            DateTimeOffset.UtcNow, 96, 96, "Test");
    }

    private static BlockInteractionCoordinator Create(
        out FakeCursorService cursor, out FakeOverlayService overlay,
        out FakeBlockPopupService popup, out FakeTranslationRouter translations)
    {
        cursor = new FakeCursorService { CursorPos = new PhysicalPoint(50, 110) };
        overlay = new FakeOverlayService();
        popup = new FakeBlockPopupService();
        translations = new FakeTranslationRouter();
        // Make the translation synchronous and cache-backed to inspect the
        // exact source selection passed to the real popup pipeline.
        translations.TranslateBlockTcs.SetResult(
            new TranslationResult("test", "test", "translated", "zh-CN", FromCache: true));
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
        return new BlockInteractionCoordinator(
            cursor, new FakeMonitorService(),
            new BlockRetryCoordinator(new FakeScreenCapture(), new FakeOcrEngine(),
                new FakeBlockSelector(), new FakeMonitorService(), settings),
            overlay, popup, translations, settings,
            NullLogger<BlockInteractionCoordinator>.Instance, new FakeEscHook());
    }

    [Fact]
    public void DragPreview_DoesNotAbsorbParallelColumn()
    {
        var ocr = MultiColumnOcr();
        var rect = BlockInteractionCoordinator.ExpandSelectedLines(
            ocr.Lines, new PhysicalPoint(50, 110), 145);

        Assert.Equal(new PhysicalRect(10, 100, 200, 60), rect);
    }

    [Fact]
    public void DragRelease_TranslatesExactlyThePreviewedRows()
    {
        using var coord = Create(out var cursor, out var overlay, out var popup, out var translations);
        var ocr = MultiColumnOcr();
        coord.SetDragStateForTest(ocr, new PhysicalPoint(50, 110),
            new PhysicalRect(10, 100, 200, 20), Monitor, 96, 96);
        coord.HandleHoldStartForTest(new PhysicalPoint(50, 110));

        cursor.CursorPos = new PhysicalPoint(50, 145);
        coord.TriggerDragTickForTest();
        var preview = overlay.UpdateCalls.Last().Box;
        coord.HandleHoldEndForTest();

        Assert.Equal(new PhysicalRect(10, 100, 200, 60), preview);
        Assert.Equal(preview, overlay.ShowCalls.Last().Box);
        Assert.Single(popup.ShowCalls);
        Assert.Equal(preview, popup.ShowCalls[0].Block.UnionBox);
        Assert.Equal("L0\nL1\nL2", popup.ShowCalls[0].Block.BlockText);
    }

    [Fact]
    public void DragRelease_UsesMousePositionEvenWhenTimerHasNotTicked()
    {
        using var coord = Create(out var cursor, out var overlay, out var popup, out _);
        var ocr = MultiColumnOcr();
        coord.SetDragStateForTest(ocr, new PhysicalPoint(50, 110),
            new PhysicalRect(10, 100, 200, 20), Monitor, 96, 96);
        coord.HandleHoldStartForTest(new PhysicalPoint(50, 110));

        cursor.CursorPos = new PhysicalPoint(50, 145);
        coord.HandleHoldEndForTest();

        Assert.Equal(new PhysicalRect(10, 100, 200, 60), overlay.ShowCalls.Last().Box);
        Assert.Equal("L0\nL1\nL2", popup.ShowCalls.Single().Block.BlockText);
    }
}
