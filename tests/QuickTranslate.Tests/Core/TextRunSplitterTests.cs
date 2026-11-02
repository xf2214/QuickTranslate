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
    private static string[] SubwordParts(string text)
    {
        return TextRunSplitter.SplitSubwords(text).Select(p => text.Substring(p.Start, p.Length)).ToArray();
    }

    [Fact]
    public void SplitSubwords_CamelAndSnakeCase()
    {
        Assert.Equal(new[] { "Selection", "Overlay", "Window" }, SubwordParts("SelectionOverlayWindow"));
        Assert.Equal(new[] { "user", "id" }, SubwordParts("user_id"));
        Assert.Equal(new[] { "on", "Button", "Click" }, SubwordParts("on_ButtonClick"));
        Assert.Equal(new[] { "MAX", "BUFFER", "SIZE" }, SubwordParts("MAX_BUFFER_SIZE"));
        Assert.Equal(new[] { "hello" }, SubwordParts("hello"));
    }

    [Fact]
    public void TrimPeripheralPunctuation_StripsOuterOnly()
    {
        var (s1, l1) = TextRunSplitter.TrimPeripheralPunctuation("hello,");
        Assert.Equal("hello", "hello,".Substring(s1, l1));

        var (s2, l2) = TextRunSplitter.TrimPeripheralPunctuation("(world)");
        Assert.Equal("world", "(world)".Substring(s2, l2));

        var (s3, l3) = TextRunSplitter.TrimPeripheralPunctuation("'apple'");
        Assert.Equal("apple", "'apple'".Substring(s3, l3));

        var (s4, l4) = TextRunSplitter.TrimPeripheralPunctuation("don't");
        Assert.Equal("don't", "don't".Substring(s4, l4));

        var (s5, l5) = TextRunSplitter.TrimPeripheralPunctuation(",.,");
        Assert.Equal(0, l5);
    }
}
