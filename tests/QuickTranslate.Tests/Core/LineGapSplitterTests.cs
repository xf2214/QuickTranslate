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
}
