using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

[Trait("Category", "Display")]
public class TranslationDisplayFormatterTests
{
    // ============================================================
    //  单词（词典结果）：字面量 \n 还原、音标拆行、释义逐行
    // ============================================================

    [Fact]
    public void ForWord_Dictionary_LiteralEscapes_BecomeLines_PhoneticOnOwnLine()
    {
        // EcdictLiteDictionary.BuildResult 的存储格式：[音标]  + 字面量 \n 分隔的释义
        var raw = "[əˈpɒl]  n. 苹果, 苹果形物\\n[医] 苹果";

        var display = TranslationDisplayFormatter.ForWord(raw, fromDictionary: true);

        Assert.Equal("[əˈpɒl]\nn. 苹果, 苹果形物\n[医] 苹果", display);
        Assert.DoesNotContain("\\n", display);
    }

    [Fact]
    public void ForWord_Dictionary_DomainTags_NotMistakenAsPhonetic()
    {
        // 无音标词条：首行 [计] 等领域标签后无两个空格，不应被当成音标拆走
        var raw = "[计] 后端, 总线允许\\nv. 访问";

        var display = TranslationDisplayFormatter.ForWord(raw, fromDictionary: true);

        Assert.Equal("[计] 后端, 总线允许\nv. 访问", display);
    }

    [Fact]
    public void ForWord_Dictionary_ExcessiveLines_TruncatedWithEllipsis()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 12).Select(i => $"n. 释义{i}"));
        var raw = $"['wɜːd]  {lines}";

        var display = TranslationDisplayFormatter.ForWord(raw, fromDictionary: true);

        var shown = display.Split('\n');
        Assert.Equal(1 + 8 + 1, shown.Length); // 音标行 + 8 条释义 + 省略号
        Assert.Equal("…", shown[^1]);
    }

    [Fact]
    public void ForWord_Online_KeepsContent_TrimsJunk()
    {
        var raw = "  这是一个示例译文。\r\n\r\n  ";

        var display = TranslationDisplayFormatter.ForWord(raw, fromDictionary: false);

        Assert.Equal("这是一个示例译文。", display);
    }

    // ============================================================
    //  句子/块：规范换行、去空行、逐行裁剪
    // ============================================================

    [Fact]
    public void ForBlock_NormalizesNewlines_RemovesBlankLines()
    {
        var raw = "  第一行译文。\r\n\r\n\n  第二行译文。  \n";

        var display = TranslationDisplayFormatter.ForBlock(raw);

        Assert.Equal("第一行译文。\n第二行译文。", display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void ForWordAndBlock_EmptyOrWhitespace_ReturnsEmpty(string? raw)
    {
        Assert.Equal(string.Empty, TranslationDisplayFormatter.ForWord(raw, fromDictionary: true));
        Assert.Equal(string.Empty, TranslationDisplayFormatter.ForWord(raw, fromDictionary: false));
        Assert.Equal(string.Empty, TranslationDisplayFormatter.ForBlock(raw));
    }
}
