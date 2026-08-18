using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Selection;

public class BlockSelectorTests
{
    private readonly IBlockSelector _selector;

    public BlockSelectorTests()
    {
        _selector = new DefaultBlockSelector();
    }

    private static OcrLayoutResult CreateOcr(params OcrLine[] lines)
    {
        var timings = new OcrTimings(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(5));
        return new OcrLayoutResult(
            new PhysicalRect(0, 0, 2000, 1500),
            lines,
            timings,
            DateTimeOffset.Now);
    }

    private static OcrLine MakeLine(int x, int y, int width, int height, string text = "text")
    {
        var words = new[] { new OcrWord(new PhysicalRect(x, y, width, height), text, 0.9f, 0) };
        return new OcrLine(new PhysicalRect(x, y, width, height), words, text);
    }

    [Fact]
    public void Case1_ThreeUniformLines_AllMerged()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Line 1");
        var line2 = MakeLine(100, 145, 800, 30, "Line 2");
        var line3 = MakeLine(100, 190, 800, 30, "Line 3");
        var ocr = CreateOcr(line1, line2, line3);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 160));

        Assert.Equal(3, result.SelectedLines.Count);
        Assert.False(result.NoBlockFound);
        Assert.Equal("Line 1\nLine 2\nLine 3", result.BlockText);
    }

    [Fact]
    public void Case2_MiddleTitleLine_BlockStops()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Body above");
        var title = MakeLine(100, 145, 800, 70, "BIG TITLE");
        var line3 = MakeLine(100, 260, 800, 30, "Body below");
        var ocr = CreateOcr(line1, title, line3);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 275));

        Assert.Single(result.SelectedLines);
        Assert.Equal("Body below", result.BlockText);
    }

    [Fact]
    public void Case3_FootnoteTooSmall_NotMerged()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Body 1");
        var line2 = MakeLine(100, 145, 800, 30, "Body 2");
        var footnote = MakeLine(100, 190, 800, 15, "Footnote 1");
        var ocr = CreateOcr(line1, line2, footnote);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 160));

        Assert.Equal(2, result.SelectedLines.Count);
        Assert.DoesNotContain(result.SelectedLines, l => l.Text == "Footnote 1");
    }

    [Fact]
    public void Case4_TwoColumn_LeftAnchor_RightNotMerged()
    {
        var left1 = MakeLine(0, 100, 900, 30, "Left 1");
        var left2 = MakeLine(0, 145, 900, 30, "Left 2");
        var right1 = MakeLine(1000, 100, 900, 30, "Right 1");
        var right2 = MakeLine(1000, 145, 900, 30, "Right 2");
        var ocr = CreateOcr(left1, left2, right1, right2);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(450, 115));

        Assert.Equal(2, result.SelectedLines.Count);
        Assert.All(result.SelectedLines, l => Assert.StartsWith("Left", l.Text));
    }

    [Fact]
    public void Case5_ListIndent_SmallDelta_Merged()
    {
        var body = MakeLine(200, 100, 700, 30, "Body text");
        var item = MakeLine(240, 145, 660, 30, "- List item");
        var ocr = CreateOcr(body, item);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(400, 115));

        Assert.Equal(2, result.SelectedLines.Count);
        Assert.Contains(result.SelectedLines, l => l.Text == "- List item");
    }

    [Fact]
    public void Case6_LargeParagraphGap_StopsGrowing()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Para1 line1");
        var line2 = MakeLine(100, 145, 800, 30, "Para1 line2");
        var para2line1 = MakeLine(100, 300, 800, 30, "Para2 line1");
        var ocr = CreateOcr(line1, line2, para2line1);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 115));

        Assert.Equal(2, result.SelectedLines.Count);
        Assert.DoesNotContain(result.SelectedLines, l => l.Text == "Para2 line1");
    }

    [Fact]
    public void Case7_AnchorSingleLine_ReturnsOnlyAnchor()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Only line");
        var ocr = CreateOcr(line1);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 115));

        Assert.Single(result.SelectedLines);
        Assert.Equal("Only line", result.BlockText);
    }

    [Fact]
    public void Case8_LowOverlapButLeftAligned_Merged()
    {
        var line1 = MakeLine(100, 100, 800, 30, "First line");
        var line2 = MakeLine(110, 145, 500, 30, "Second shorter");
        var ocr = CreateOcr(line1, line2);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 115));

        Assert.Equal(2, result.SelectedLines.Count);
    }

    [Fact]
    public void Case9_LowOverlapAndHugeDelta_Rejected()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Aligned left");
        var line2 = MakeLine(800, 145, 500, 30, "Far right");
        var ocr = CreateOcr(line1, line2);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 115));

        Assert.Single(result.SelectedLines);
        Assert.DoesNotContain(result.SelectedLines, l => l.Text == "Far right");
    }

    [Fact]
    public void Case10_MedianHeightWithTitle_TitleExcluded()
    {
        var heights = new[] { 28, 30, 32, 36, 200 };
        var lines = new List<OcrLine>();
        int y = 100;
        foreach (var h in heights)
        {
            lines.Add(MakeLine(100, y, 800, h, $"H{h}"));
            y += h + 15;
        }
        var ocr = CreateOcr(lines.ToArray());

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, lines[2].Box.Top + 16));

        Assert.DoesNotContain(result.SelectedLines, l => l.Text == "H200");
    }

    [Fact]
    public void Case11_EmptyLines_NoBlockFound()
    {
        var ocr = CreateOcr();

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(0, 0));

        Assert.True(result.NoBlockFound);
        Assert.Null(result.BlockText);
        Assert.Empty(result.SelectedLines);
    }

    [Fact]
    public void Case12_UnionBoxNearLeftEdge_Detectable()
    {
        var line1 = MakeLine(10, 100, 800, 30, "Near edge line");
        var ocr = CreateOcr(line1);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(100, 115));

        Assert.Equal(10, result.UnionBox.Left);
    }

    [Fact]
    public void Case13_DownwardFail_NoBacktrack()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Good line 1");
        var line2 = MakeLine(100, 145, 800, 100, "Bad big line");
        var line3 = MakeLine(100, 290, 800, 30, "Good line 3 but skipped");
        var ocr = CreateOcr(line1, line2, line3);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 115));

        Assert.Single(result.SelectedLines);
        Assert.DoesNotContain(result.SelectedLines, l => l.Text == "Good line 3 but skipped");
    }

    [Fact]
    public void Case14_AllWhitespaceLines_NoBlockFound()
    {
        var line1 = MakeLine(100, 100, 800, 30, "   ");
        var line2 = MakeLine(100, 145, 800, 30, "\t");
        var ocr = CreateOcr(line1, line2);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 115));

        Assert.True(result.NoBlockFound);
        Assert.Null(result.BlockText);
    }

    [Fact]
    public void Case15_OperationIdAndKind_AreCorrect()
    {
        var line1 = MakeLine(100, 100, 800, 30, "Test");
        var ocr = CreateOcr(line1);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 115));

        Assert.NotEqual(Guid.Empty, result.OperationId);
        Assert.Equal(SelectionKind.Block, result.Kind);
    }

    [Fact]
    public void Case16_MaxLinesPerBlock_LimitedTo30()
    {
        var lines = new List<OcrLine>();
        int y = 100;
        for (int i = 0; i < 300; i++)
        {
            lines.Add(MakeLine(100, y, 800, 30, $"Line {i}"));
            y += 45;
        }
        var ocr = CreateOcr(lines.ToArray());
        var anchorLine = lines[150];
        var anchor = new PhysicalPoint(500, anchorLine.Box.Top + 15);

        var result = _selector.SelectBlock(ocr, anchor);

        Assert.Equal(SelectionOptions.Default.BlockMaxLinesPerBlock, result.SelectedLines.Count);
    }

    [Fact]
    public void Case17_AnchorFarFromAllLines_NoBlockFound()
    {
        // 光标落在远离所有行的空白区 → 不应把不相近的段落误当目标块
        var line1 = MakeLine(100, 100, 800, 30, "Far paragraph");
        var ocr = CreateOcr(line1);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 400));

        Assert.True(result.NoBlockFound);
        Assert.Null(result.BlockText);
    }

    [Fact]
    public void Case18_AnchorNearLine_StillAnchors()
    {
        // 光标在行附近（未超出距离上限）→ 仍可锚定该行
        var line1 = MakeLine(100, 100, 800, 30, "Near paragraph");
        var ocr = CreateOcr(line1);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(500, 145));

        Assert.False(result.NoBlockFound);
        Assert.Equal("Near paragraph", result.BlockText);
    }

    [Fact]
    public void Case19_WideUiBarAdjacent_NotAbsorbed()
    {
        // 紧邻段落的全宽 UI 栏（宽度远超正文行）不应被吸入块
        var line1 = MakeLine(100, 100, 800, 30, "para one");
        var line2 = MakeLine(100, 138, 780, 30, "para two");
        var wideBar = MakeLine(100, 176, 2400, 30, "FULL WIDTH TOOLBAR");
        var ocr = CreateOcr(line1, line2, wideBar);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(150, 115));

        Assert.False(result.NoBlockFound);
        Assert.Equal(2, result.SelectedLines.Count);
        Assert.DoesNotContain("TOOLBAR", result.BlockText, StringComparison.Ordinal);
    }

    [Fact]
    public void Case20_ShortAnchorLine_FullWidthParagraphStillAbsorbed()
    {
        // 锚点落在段末短行时，同段落的全宽正文行仍应被吸入（基准取中位行宽）
        var line1 = MakeLine(100, 100, 800, 30, "full line one");
        var line2 = MakeLine(100, 138, 820, 30, "full line two");
        var tail = MakeLine(100, 176, 200, 30, "tail");
        var ocr = CreateOcr(line1, line2, tail);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(150, 190));

        Assert.False(result.NoBlockFound);
        Assert.Equal(3, result.SelectedLines.Count);
    }

    [Fact]
    public void Case21_NarrowAnchor_DisjointNextLineLeftAligned_NotMerged()
    {
        // 窄锚点行右侧紧邻但不相交的行：旧逻辑下 leftDelta 很小即可通过 OR 判定被吸入，
        // 水平相交护栏要求必须真正相交才能生长。
        var narrow = MakeLine(100, 100, 30, 30, "tag");
        var disjoint = MakeLine(140, 145, 500, 30, "disjoint content");
        var ocr = CreateOcr(narrow, disjoint);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(115, 115));

        Assert.Single(result.SelectedLines);
        Assert.DoesNotContain(result.SelectedLines, l => l.Text == "disjoint content");
    }

    [Fact]
    public void Case22_SameHeightSeparateColumn_NotMerged()
    {
        // 同一垂直范围但水平完全分离的另一栏：verticalGap 为负不能绕过水平条件
        var left = MakeLine(100, 100, 400, 30, "left column");
        var right = MakeLine(900, 110, 400, 30, "right column");
        var ocr = CreateOcr(left, right);

        var result = _selector.SelectBlock(ocr, new PhysicalPoint(200, 115));

        Assert.Single(result.SelectedLines);
        Assert.Equal("left column", result.BlockText);
    }
}
