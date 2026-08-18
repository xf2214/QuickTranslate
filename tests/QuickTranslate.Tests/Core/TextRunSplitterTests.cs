using QuickTranslate.Core.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class TextRunSplitterTests
{
    private static string[] RunTexts(string token)
    {
        return TextRunSplitter.Split(token).Select(r => r.Slice(token)).ToArray();
    }

    [Fact]
    public void IdentifierWithTrailingSemicolon_SplitsPunctuation()
    {
        var runs = TextRunSplitter.Split("MinSegmentWidth;");
        Assert.Equal(2, runs.Count);
        Assert.Equal(TextRunSplitter.CharClass.Word, runs[0].Class);
        Assert.Equal("MinSegmentWidth", runs[0].Slice("MinSegmentWidth;"));
        Assert.Equal(TextRunSplitter.CharClass.Punct, runs[1].Class);
        Assert.Equal(";", runs[1].Slice("MinSegmentWidth;"));
    }

    [Fact]
    public void CjkGluedToIdentifier_SplitsByScript()
    {
        var token = "增TrySegmentConstrained)";
        var runs = TextRunSplitter.Split(token);
        Assert.Equal(3, runs.Count);
        Assert.Equal(TextRunSplitter.CharClass.Cjk, runs[0].Class);
        Assert.Equal(TextRunSplitter.CharClass.Word, runs[1].Class);
        Assert.Equal(TextRunSplitter.CharClass.Punct, runs[2].Class);
        Assert.Equal(new[] { "增", "TrySegmentConstrained", ")" }, RunTexts(token));
    }

    [Fact]
    public void ApostropheInsideWord_NotSplit()
    {
        Assert.Single(TextRunSplitter.Split("don't"));
        Assert.Single(TextRunSplitter.Split("well-known"));
    }

    [Fact]
    public void TrailingApostrophe_SplitsOff()
    {
        // 词尾撇号（code'）不属于词内缩合，应拆出
        var runs = TextRunSplitter.Split("code'");
        Assert.Equal(2, runs.Count);
        Assert.Equal("code", runs[0].Slice("code'"));
    }

    [Fact]
    public void PureCjkRun_KeptTogether()
    {
        var runs = TextRunSplitter.Split("中文文本");
        Assert.Single(runs);
        Assert.Equal(TextRunSplitter.CharClass.Cjk, runs[0].Class);
    }

    [Fact]
    public void EmptyInput_NoRuns()
    {
        Assert.Empty(TextRunSplitter.Split(""));
    }

    [Fact]
    public void CharWeight_CjkWiderThanLatin_PunctNarrower()
    {
        Assert.Equal(2.0, TextRunSplitter.CharWeight('中'));
        Assert.Equal(1.0, TextRunSplitter.CharWeight('a'));
        Assert.Equal(0.6, TextRunSplitter.CharWeight(';'));
        Assert.Equal(2.0, TextRunSplitter.CharWeight('！')); // 全角标点
    }

    private static string[] CamelParts(string text)
    {
        return TextRunSplitter.SplitCamelCase(text).Select(p => text.Substring(p.Start, p.Length)).ToArray();
    }

    [Fact]
    public void SplitCamelCase_PascalAndCamelAndAcronym()
    {
        Assert.Equal(new[] { "Line", "Text" }, CamelParts("LineText"));
        Assert.Equal(new[] { "get", "Value2" }, CamelParts("getValue2"));
        Assert.Equal(new[] { "HTML", "Parser" }, CamelParts("HTMLParser"));
        // 无大小写边界：原样返回
        Assert.Equal(new[] { "lowercase" }, CamelParts("lowercase"));
        Assert.Equal(new[] { "UPPER" }, CamelParts("UPPER"));
    }
}
