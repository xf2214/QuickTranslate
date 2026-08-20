using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Selection;

public class WordSelectorTests
{
    private readonly IWordBoxResolver _resolver = new DefaultWordBoxResolver();
    private readonly IWordSelector _selector;

    public WordSelectorTests()
    {
        _selector = new WordSelector(_resolver);
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
            new PhysicalRect(0, 0, 1000, 800),
            lines,
            timings,
            DateTimeOffset.Now);
    }

    [Fact]
    public void Case1_AnchorInsideWord_ReturnsThatWord()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 300, 40),
            new[]
            {
                new OcrWord(new PhysicalRect(0, 0, 100, 40), "First", 0.9f, 0),
                new OcrWord(new PhysicalRect(110, 0, 80, 40), "Second", 0.9f, 0),
                new OcrWord(new PhysicalRect(200, 0, 100, 40), "Third", 0.9f, 0)
            });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(150, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("Second", result.Text);
        Assert.Equal("First Second Third", result.ContextLine);
    }

    [Fact]
    public void Case1b_CursorVerticallyOutsideTightenedWordBox_StillSelectsSameLineWord()
    {
        // 词框垂直收紧后只贴墨水（y 5..17），光标在行内但词框垂直范围外（y=32）：
        // 旧行为会退到“最近词”选中邻行，宽容判定应仍命中同一行水平指向的词。
        var lineA = new OcrLine(
            new PhysicalRect(0, 0, 300, 30),
            new[] { new OcrWord(new PhysicalRect(100, 5, 80, 12), "target", 0.9f, 0) });
        var lineB = new OcrLine(
            new PhysicalRect(0, 40, 300, 30),
            new[] { new OcrWord(new PhysicalRect(120, 45, 60, 12), "near", 0.9f, 1) });
        var ocr = CreateOcr(lineA, lineB);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(140, 32));

        Assert.False(result.NoTextFound);
        Assert.Equal("target", result.Text);
    }

    [Fact]
    public void Case2_TwoWordsContainAnchor_PicksSmallerArea()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 200, 40), "BigWord", 0.9f, 0),
            new OcrWord(new PhysicalRect(50, 10, 50, 20), "Tiny", 0.9f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 200, 40), words);
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(70, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("Tiny", result.Text);
    }

    [Fact]
    public void Case3_NoContains_TwoNearbyCandidates_PicksCloser()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 300, 40),
            new[]
            {
                new OcrWord(new PhysicalRect(0, 0, 100, 40), "Left", 0.9f, 0),
                new OcrWord(new PhysicalRect(130, 0, 100, 40), "Right", 0.9f, 0)
            });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(120, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("Right", result.Text);
    }

    [Fact]
    public void Case4_AnchorLeftOfFirstWord_10px_SelectsFirstWord()
    {
        var line = new OcrLine(
            new PhysicalRect(50, 0, 200, 40),
            new[]
            {
                new OcrWord(new PhysicalRect(50, 0, 80, 40), "Hello", 0.9f, 0),
                new OcrWord(new PhysicalRect(140, 0, 80, 40), "World", 0.9f, 0)
            });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(40, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public void Case5_AnchorRightOfLastWord_20px_SelectsLastWord()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 300, 40),
            new[]
            {
                new OcrWord(new PhysicalRect(0, 0, 100, 40), "First", 0.9f, 0),
                new OcrWord(new PhysicalRect(200, 0, 100, 40), "Last", 0.9f, 0)
            });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(320, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("Last", result.Text);
    }

    [Fact]
    public void Case6_AnchorFarFromAll_NoTextFound()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 300, 40),
            new[]
            {
                new OcrWord(new PhysicalRect(0, 0, 100, 40), "A", 0.9f, 0)
            });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(1000, 1000));

        Assert.True(result.NoTextFound);
        Assert.Null(result.Text);
    }

    [Fact]
    public void Case7_TwoLines_AnchorBetween_PicksCloserLineWord()
    {
        var line1 = new OcrLine(
            new PhysicalRect(50, 0, 200, 40),
            new[] { new OcrWord(new PhysicalRect(50, 0, 200, 40), "Line1Word", 0.9f, 0) });
        var line2 = new OcrLine(
            new PhysicalRect(50, 60, 200, 40),
            new[] { new OcrWord(new PhysicalRect(50, 60, 200, 40), "Line2Word", 0.9f, 1) });
        var ocr = CreateOcr(line1, line2);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(100, 50));

        Assert.False(result.NoTextFound);
        Assert.Equal("Line2Word", result.Text);
    }

    [Fact]
    public void Case8_DynamicMax_LargeLineHeight_Hits()
    {
        var opts = new SelectionOptions();
        var line = new OcrLine(
            new PhysicalRect(0, 0, 200, 80),
            new[] { new OcrWord(new PhysicalRect(0, 0, 200, 80), "BigLine", 0.9f, 0) });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(280, 40), opts);

        Assert.False(result.NoTextFound);
        Assert.Equal("BigLine", result.Text);
    }

    [Fact]
    public void Case8_DynamicMax_SmallLineHeight_Misses()
    {
        var opts = new SelectionOptions();
        var line = new OcrLine(
            new PhysicalRect(0, 0, 200, 16),
            new[] { new OcrWord(new PhysicalRect(0, 0, 200, 16), "SmallLine", 0.9f, 0) });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(280, 8), opts);

        Assert.True(result.NoTextFound);
    }

    [Fact]
    public void Case9_ConfidenceTooLow_Filtered_NoTextFound()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 200, 40),
            new[] { new OcrWord(new PhysicalRect(0, 0, 200, 40), "LowConf", 0.2f, 0) });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(50, 20));

        Assert.True(result.NoTextFound);
    }

    [Fact]
    public void Case10_WidthTooSmall_Filtered_NoTextFound()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 200, 40),
            new[] { new OcrWord(new PhysicalRect(0, 0, 2, 40), "Tiny", 0.9f, 0) });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(0, 20));

        Assert.True(result.NoTextFound);
    }

    [Fact]
    public void Case11_ResultProperties_AreCorrect()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 200, 40),
            new[] { new OcrWord(new PhysicalRect(10, 5, 80, 30), "Target", 0.75f, 0) },
            text: "Context Line Text");
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(50, 20));

        Assert.Equal(SelectionKind.Word, result.Kind);
        Assert.Equal("Context Line Text", result.ContextLine);
        Assert.NotEqual(Guid.Empty, result.OperationId);
        Assert.Equal(0.75f, result.Confidence);
        Assert.Equal(new PhysicalRect(10, 5, 80, 30), result.Box);
    }

    [Fact]
    public void Case12_MultipleH1_SameArea_TieBreakByCenterDistance()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 100, 40), "Left", 0.9f, 0),
            new OcrWord(new PhysicalRect(60, 0, 100, 40), "Right", 0.9f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 200, 40), words);
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(90, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("Right", result.Text);
    }

    [Fact]
    public void Case13_CjkWord_AnchorInside_SelectsIt()
    {
        // Alt+1 取词需支持中文（用户实际工作流悬停中文文本）
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 40, 40), "识", 0.9f, 0),
            new OcrWord(new PhysicalRect(40, 0, 40, 40), "别", 0.9f, 0),
            new OcrWord(new PhysicalRect(80, 0, 40, 40), "框", 0.9f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 120, 40), words);
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(60, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("别", result.Text);
        Assert.Equal(new PhysicalRect(40, 0, 40, 40), result.Box);
    }

    [Fact]
    public void Case14_PurePunctuationAndDigits_StillFiltered()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 100, 40), "123.45", 0.9f, 0),
            new OcrWord(new PhysicalRect(110, 0, 60, 40), "----", 0.9f, 0),
            new OcrWord(new PhysicalRect(180, 0, 60, 40), "///", 0.9f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 240, 40), words);
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(50, 20));

        Assert.True(result.NoTextFound);
    }

    [Fact]
    public void Case15_AbnormallyWideBox_Rejected_NoTextFound()
    {
        // 日志复现：比例法兜底把短文本摊到整行宽（Text='tes' Box=542x51），
        // 单字符宽远超行高上限 → 拒绝，不再画出超大选框
        var line = new OcrLine(
            new PhysicalRect(0, 0, 542, 51),
            new[] { new OcrWord(new PhysicalRect(0, 0, 542, 51), "tes", 0.9f, 0) });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(286, 20));

        Assert.True(result.NoTextFound);
    }

    [Fact]
    public void Case16_WideBoxRejected_FallsBackToNearbyValidWord()
    {
        // 异常宽框被拒后，应退而选择附近几何合理的候选
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 650, 60), "y", 0.9f, 0),
            new OcrWord(new PhysicalRect(0, 65, 120, 30), "valid", 0.9f, 1)
        };
        var line1 = new OcrLine(new PhysicalRect(0, 0, 650, 60), new[] { words[0] });
        var line2 = new OcrLine(new PhysicalRect(0, 65, 120, 30), new[] { words[1] });
        var ocr = CreateOcr(line1, line2);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(60, 40));

        Assert.False(result.NoTextFound);
        Assert.Equal("valid", result.Text);
    }

    [Fact]
    public void Case17_LongWordWithinWidthLimit_StillSelected()
    {
        // 长标识符（如 ProjectionWordSegmenter）单字符宽未超限 → 不受新过滤影响
        var line = new OcrLine(
            new PhysicalRect(0, 0, 261, 31),
            new[] { new OcrWord(new PhysicalRect(0, 0, 261, 31), "ProjectionWordSegmenter", 0.9f, 0) });
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(130, 15));

        Assert.False(result.NoTextFound);
        Assert.Equal("ProjectionWordSegmenter", result.Text);
    }

    [Fact]
    public void Case18_TolerantHitsOnTwoLines_PicksNearestWord_NotSmallestArea()
    {
        // 光标落在两行容差重叠区、且不在任何词框内：
        // 旧逻辑按面积取小会选中更远一行的小词（选框偏离鼠标），
        // 新逻辑按到词框距离取近。
        var line1 = new OcrLine(
            new PhysicalRect(0, 0, 300, 26),
            new[] { new OcrWord(new PhysicalRect(0, 0, 100, 6), "upper", 0.9f, 0) });
        var line2 = new OcrLine(
            new PhysicalRect(0, 18, 300, 26),
            new[] { new OcrWord(new PhysicalRect(0, 36, 160, 8), "lower", 0.9f, 1) });
        var ocr = CreateOcr(line1, line2);

        // (80,29)：两行容差都命中；upper 面积更小但距离 24px，lower 距离 7px
        var result = _selector.SelectWord(ocr, new PhysicalPoint(80, 29));

        Assert.False(result.NoTextFound);
        Assert.Equal("lower", result.Text);
    }

    [Fact]
    public void Case19_NestedBoxesInsideHit_StillPicksSmallerArea()
    {
        // 光标真正落在多个嵌套词框内时保留面积优先（选更精确的内框）
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 200, 40), "Outer", 0.9f, 0),
            new OcrWord(new PhysicalRect(60, 10, 60, 20), "Inner", 0.9f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 200, 40), words);
        var ocr = CreateOcr(line);

        var result = _selector.SelectWord(ocr, new PhysicalPoint(85, 20));

        Assert.False(result.NoTextFound);
        Assert.Equal("Inner", result.Text);
    }
}
