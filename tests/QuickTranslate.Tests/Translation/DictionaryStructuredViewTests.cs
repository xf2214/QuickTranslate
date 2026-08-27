using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

[Trait("Category", "Display")]
public class DictionaryStructuredViewTests
{
    [Fact]
    public void Parse_WithPhonetic_ExtractsPhoneticAndLines()
    {
        var raw = "[əˈpɒl]  n. 苹果\\n[医] 苹果";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.Equal("əˈpɒl", view.Phonetic);
        Assert.Equal(2, view.Lines.Count);
        Assert.Equal("n.", view.Lines[0].PosTag);
        Assert.Equal("苹果", view.Lines[0].Body);
        Assert.Null(view.Lines[0].DomainTag);
        Assert.False(view.Lines[0].IsTruncationMarker);
        Assert.Null(view.Lines[1].PosTag);
        Assert.Equal("[医]", view.Lines[1].DomainTag);
        Assert.Equal("苹果", view.Lines[1].Body);
    }

    [Fact]
    public void Parse_WithoutPhonetic_PhoneticNull()
    {
        var raw = "[计] 后端, 总线允许\\nv. 访问";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.Null(view.Phonetic);
        Assert.Equal(2, view.Lines.Count);
        Assert.Null(view.Lines[0].PosTag);
        Assert.Equal("[计]", view.Lines[0].DomainTag);
        Assert.Equal("后端, 总线允许", view.Lines[0].Body);
        Assert.Equal("v.", view.Lines[1].PosTag);
    }

    [Fact]
    public void Parse_DomainTag_NotMistakenAsPos()
    {
        var raw = "[计] 后端";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.Single(view.Lines);
        Assert.Null(view.Lines[0].PosTag);
        Assert.Equal("[计]", view.Lines[0].DomainTag);
    }

    [Fact]
    public void Parse_PlainLine_NoTags()
    {
        var raw = "这是一行纯文本";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.Null(view.Phonetic);
        Assert.Single(view.Lines);
        Assert.Null(view.Lines[0].PosTag);
        Assert.Null(view.Lines[0].DomainTag);
        Assert.Equal("这是一行纯文本", view.Lines[0].Body);
    }

    [Fact]
    public void Parse_SingleLine_ShouldNumber_False()
    {
        var raw = "n. 释义";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.False(view.ShouldNumberLines);
    }

    [Fact]
    public void Parse_MultipleLines_ShouldNumber_True()
    {
        var raw = "n. 释义1\\nn. 释义2";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.True(view.ShouldNumberLines);
        Assert.Equal(2, view.Lines.Count);
    }

    [Fact]
    public void Parse_ExceedsEightLines_CappedWithTruncationMarker()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 12).Select(i => $"n. 释义{i}"));
        var raw = $"['wɜːd]  {lines}";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.Equal("'wɜːd", view.Phonetic);
        // 8 real lines + 1 truncation marker
        Assert.Equal(9, view.Lines.Count);
        Assert.True(view.IsTruncated);
        Assert.True(view.Lines[^1].IsTruncationMarker);
        Assert.Equal("…", view.Lines[^1].Body);
        Assert.Equal(8, view.Lines.Count(l => !l.IsTruncationMarker));
    }

    [Fact]
    public void Parse_ExactlyEightLines_NoTruncation()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 8).Select(i => $"n. 释义{i}"));
        var view = DictionaryStructuredView.Parse(lines);
        Assert.Equal(8, view.Lines.Count);
        Assert.False(view.IsTruncated);
        Assert.False(view.Lines[^1].IsTruncationMarker);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        var view = DictionaryStructuredView.Parse(null);
        Assert.Null(view.Phonetic);
        Assert.Empty(view.Lines);
        Assert.False(view.IsTruncated);
        Assert.False(view.ShouldNumberLines);

        var view2 = DictionaryStructuredView.Parse("   ");
        Assert.Empty(view2.Lines);

        var view3 = DictionaryStructuredView.Parse("");
        Assert.Empty(view3.Lines);
    }

    [Fact]
    public void Parse_PosTagVariants()
    {
        var raw = "pron. 我\\nvt. 及物\\nadj. 美丽的\\nadv. 快速地";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.Equal("pron.", view.Lines[0].PosTag);
        Assert.Equal("vt.", view.Lines[1].PosTag);
        Assert.Equal("adj.", view.Lines[2].PosTag);
        Assert.Equal("adv.", view.Lines[3].PosTag);
    }

    [Fact]
    public void Parse_PhoneticLiteralEscapesHandled()
    {
        var raw = "[əˈpɒl]  n. 苹果, 苹果形物\\n[医] 苹果";
        var view = DictionaryStructuredView.Parse(raw);
        Assert.Equal(2, view.Lines.Count);
        Assert.DoesNotContain("\\n", view.Lines[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ForWord_RemainsUnchanged_Dictionary()
    {
        var raw = "[əˈpɒl]  n. 苹果, 苹果形物\\n[医] 苹果";
        var display = TranslationDisplayFormatter.ForWord(raw, fromDictionary: true);
        Assert.Equal("[əˈpɒl]\nn. 苹果, 苹果形物\n[医] 苹果", display);
    }

    [Fact]
    public void ForWord_RemainsUnchanged_Truncation()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 12).Select(i => $"n. 释义{i}"));
        var raw = $"['wɜːd]  {lines}";
        var display = TranslationDisplayFormatter.ForWord(raw, fromDictionary: true);
        var shown = display.Split('\n');
        Assert.Equal(1 + 8 + 1, shown.Length);
        Assert.Equal("…", shown[^1]);
    }
}
