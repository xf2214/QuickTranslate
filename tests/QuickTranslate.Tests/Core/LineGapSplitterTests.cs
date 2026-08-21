using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class LineGapSplitterTests
{
    private static OcrLine MakeLine(int x, int y, int width, int height, params (int OffsetX, int Width, string Text)[] words)
    {
        var wordList = words
            .Select(w => new OcrWord(new PhysicalRect(x + w.OffsetX, y, w.Width, height), w.Text, 0.9f, 0))
            .ToList();
        return new OcrLine(new PhysicalRect(x, y, width, height), wordList, "line");
    }

    [Fact]
    public void BigGapBetweenClusters_SplitsIntoTwoLines()
    {
        // 模拟 det 把左右两页文字合并成一个检测框：中间 400px 空白
        var line = MakeLine(100, 300, 1000, 30,
            (0, 300, "left page text"),
            (700, 300, "right page text"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Equal(2, result.Count);
        Assert.Equal("left page text", result[0].Text);
        Assert.Equal("right page text", result[1].Text);
        Assert.Equal(100, result[0].Box.Left);
        Assert.Equal(400, result[0].Box.Right);
        Assert.Equal(800, result[1].Box.Left);
        Assert.Equal(1100, result[1].Box.Right);
    }

    [Fact]
    public void NormalWordSpacing_NotSplit()
    {
        // 正常词间距（约 0.3 倍行高）不应拆分
        var line = MakeLine(100, 300, 500, 30,
            (0, 100, "hello"),
            (110, 100, "world"),
            (220, 100, "again"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Single(result);
        Assert.Same(line, result[0]);
    }

    [Fact]
    public void SingleWordLine_NotSplit()
    {
        var line = MakeLine(100, 300, 800, 30, (0, 200, "solo"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Single(result);
        Assert.Same(line, result[0]);
    }

    [Fact]
    public void UnorderedWords_SplitByXOrder()
    {
        // 词序乱序时按 X 排序后再判断空隙
        var line = MakeLine(100, 300, 1000, 30,
            (700, 300, "right"),
            (0, 300, "left"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Equal(2, result.Count);
        Assert.Equal("left", result[0].Text);
        Assert.Equal("right", result[1].Text);
    }

    [Fact]
    public void WideCjkChars_ModerateGap_NotOverSplit()
    {
        // CJK/大字号下字宽接近行高：中等空隙（超过行高项阈值但小于字符宽项阈值）
        // 不应误拆（避免把正常排版的宽字距文本切碎）
        var line = MakeLine(100, 300, 600, 30,
            (0, 200, "中文"),
            (320, 200, "文本"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Single(result);
    }

    [Fact]
    public void WideCjkChars_LargeGap_Splits()
    {
        // 同上字宽，但空隙足够大（跨页空白）→ 仍应拆分
        var line = MakeLine(100, 300, 1000, 30,
            (0, 200, "中文"),
            (700, 200, "文本"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Equal(2, result.Count);
        Assert.Equal("中文", result[0].Text);
        Assert.Equal("文本", result[1].Text);
    }

    [Fact]
    public void CjkChars_GapJustOverCjkThreshold_Splits()
    {
        // CJK 相邻时阈值用紧因子 1.25×：gap 130 > ceil(100×1.25)=125 → 拆分。
        // 旧 4× 因子阈值会是 400，这种分栏/跨区域断开拆不开（选区跨区域连通）。
        var line = MakeLine(100, 300, 800, 30,
            (0, 200, "中文"),
            (330, 200, "文本"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Equal(2, result.Count);
        Assert.Equal("中文", result[0].Text);
        Assert.Equal("文本", result[1].Text);
    }

    [Fact]
    public void CjkLatinMixed_WideGapFactor_StillProtected()
    {
        // 空隙一侧是拉丁文字（如 “中文 ABC”）时仍用宽因子 4×，不误拆混排宽词距
        var line = MakeLine(100, 300, 800, 30,
            (0, 200, "中文"),
            (330, 200, "ABC"));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Single(result);
    }

    [Fact]
    public void ColumnGutter_TwoLineHeights_Splits()
    {
        // 分栏栏间距（约 2 倍行高，未达旧阈值 2.5 倍）也应拆开：
        // 否则行框横跨栏间空白，选区“中间断开却连通到附近其他文本”。
        // 两侧用多字符窄字宽词，避免字符宽项抬高阈值。
        var line = MakeLine(100, 300, 800, 30,
            (0, 300, new string('a', 30)),
            (360, 300, new string('b', 30)));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Equal(2, result.Count);
        Assert.Equal(100, result[0].Box.Left);
        Assert.Equal(400, result[0].Box.Right);
        Assert.Equal(460, result[1].Box.Left);
    }

    [Fact]
    public void JustifiedWideTracking_OneLineHeight_NotSplit()
    {
        // 两端对齐/宽字距排版的词间距（约 1 倍行高以内）不应误拆
        var line = MakeLine(100, 300, 800, 30,
            (0, 300, new string('a', 30)),
            (325, 300, new string('b', 30)));

        var result = LineGapSplitter.SplitLines(new[] { line });

        Assert.Single(result);
    }
}
