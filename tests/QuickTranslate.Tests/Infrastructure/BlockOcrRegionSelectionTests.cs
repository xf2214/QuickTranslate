using QuickTranslate.Core.Geometry;
using QuickTranslate.Infrastructure.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class BlockOcrRegionSelectionTests
{
    [Fact]
    public void BlockFocus_RecognizesOnlyAnchorColumnRatherThanBothPageColumns()
    {
        // A vertical-only focus band causes eight expensive recognizer passes.
        // Four of the rows belong to an unrelated neighboring column.
        var boxes = new List<PhysicalRect>
        {
            new(10, 100, 270, 20), new(510, 100, 270, 20),
            new(10, 130, 270, 20), new(510, 130, 270, 20),
            new(10, 160, 270, 20), new(510, 160, 270, 20),
            new(10, 190, 270, 20), new(510, 190, 270, 20),
        };

        var kept = OcrRegionSelector.FilterByFocusBand(
            boxes, new PhysicalRect(0, 0, 1200, 720),
            new PhysicalRect(79, -130, 2, 560));

        Assert.Equal(new[] { boxes[0], boxes[2], boxes[4], boxes[6] }, kept);
    }

    [Fact]
    public void BlockFocus_PreservesIndentedAndShortLinesFromAnchorColumn()
    {
        var boxes = new List<PhysicalRect>
        {
            new(60, 130, 240, 20),  // anchor
            new(80, 160, 120, 20),  // indented short line
            new(60, 190, 50, 20),   // short paragraph tail
            new(600, 130, 240, 20), // other column
        };

        var kept = OcrRegionSelector.FilterByFocusBand(
            boxes, new PhysicalRect(0, 0, 1200, 720),
            new PhysicalRect(100, -130, 2, 560));

        Assert.Equal(new[] { boxes[0], boxes[1], boxes[2] }, kept);
    }

    [Fact]
    public void BlockFocus_WhenAnchorNotNearDetectedText_PreservesCandidatesForFallback()
    {
        var boxes = new List<PhysicalRect>
        {
            new(30, 100, 210, 20),
            new(500, 130, 210, 20),
        };

        var kept = OcrRegionSelector.FilterByFocusBand(
            boxes, new PhysicalRect(0, 0, 1200, 720),
            new PhysicalRect(1080, -130, 2, 560));

        Assert.Equal(boxes, kept);
    }

    [Fact]
    public void WordFocus_FullWidthDoesNotDiscardParallelText()
    {
        // Word mode uses a full-width focus strip, not the 2px-wide anchor marker.
        var boxes = new List<PhysicalRect>
        {
            new(30, 100, 210, 20),
            new(500, 100, 210, 20),
        };

        var kept = OcrRegionSelector.FilterByFocusBand(
            boxes, new PhysicalRect(0, 0, 1200, 720),
            new PhysicalRect(0, 80, 1200, 60));

        Assert.Equal(boxes, kept);
    }
}
