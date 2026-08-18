using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class LeadingGlyphCleanerTests
{
    private static OcrLine MakeLine(int y, int height, params (int X, int Width, string Text)[] words)
    {
        var wordList = words
            .Select(w => new OcrWord(new PhysicalRect(w.X, y, w.Width, height), w.Text, 0.9f, 0))
            .ToList();
        int left = words.Min(w => w.X);
        int right = words.Max(w => w.X + w.Width);
        return new OcrLine(new PhysicalRect(left, y, right - left, height), wordList, "line");
    }

    [Fact]
    public void LeadingQuestionMarkWithGap_Removed()
    {
        // 图标 ? 与正文之间有空隙 → 移除并收紧行框
        var line = MakeLine(100, 26, (0, 12, "?"), (30, 300, "Text-to-speech"));

        var result = LeadingGlyphCleaner.Clean(new[] { line }, out int cleaned);

        Assert.Equal(1, cleaned);
        Assert.Single(result);
        Assert.Equal("Text-to-speech", result[0].Text);
        Assert.Equal(30, result[0].Box.Left);
        Assert.Equal(330, result[0].Box.Right);
    }

    [Fact]
    public void LeadingDigitBeforeCjkWithBigGap_Removed()
    {
        // 图标误识别为 0（空隙 ≥ 行高/4）→ 移除
        var line = MakeLine(100, 40, (0, 20, "0"), (60, 300, "中文文本"));

        var result = LeadingGlyphCleaner.Clean(new[] { line }, out int cleaned);

        Assert.Equal(1, cleaned);
        Assert.Equal("中文文本", result[0].Text);
    }

    [Fact]
    public void LeadingDigitWithSmallGap_Kept()
    {
        // 数字空隙不足（行高/4）→ 保留，避免误伤代码行
        var line = MakeLine(100, 40, (0, 20, "1"), (25, 300, "value"));

        var result = LeadingGlyphCleaner.Clean(new[] { line }, out int cleaned);

        Assert.Equal(0, cleaned);
        Assert.Same(line, result[0]);
    }

    [Fact]
    public void LeadingSymbolNoGap_Kept()
    {
        // # 紧贴正文（#include 场景）→ 保留
        var line = MakeLine(100, 26, (0, 10, "#"), (11, 200, "include"));

        var result = LeadingGlyphCleaner.Clean(new[] { line }, out int cleaned);

        Assert.Equal(0, cleaned);
        Assert.Same(line, result[0]);
    }

    [Fact]
    public void LeadingLetterWithGap_Kept()
    {
        // 首词是字母（非符号）→ 保留
        var line = MakeLine(100, 26, (0, 10, "a"), (30, 200, "list item"));

        var result = LeadingGlyphCleaner.Clean(new[] { line }, out int cleaned);

        Assert.Equal(0, cleaned);
        Assert.Same(line, result[0]);
    }

    [Fact]
    public void MultiCharFirstWord_Kept()
    {
        var line = MakeLine(100, 26, (0, 40, "0x4D"), (60, 200, "BF"));

        var result = LeadingGlyphCleaner.Clean(new[] { line }, out int cleaned);

        Assert.Equal(0, cleaned);
        Assert.Same(line, result[0]);
    }

    [Fact]
    public void SingleWordLine_Kept()
    {
        var line = MakeLine(100, 26, (0, 12, "?"));

        var result = LeadingGlyphCleaner.Clean(new[] { line }, out int cleaned);

        Assert.Equal(0, cleaned);
        Assert.Same(line, result[0]);
    }
}
