using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Selection;

public class WordBoxResolverTests
{
    private readonly DefaultWordBoxResolver _resolver = new();

    [Fact]
    public void Resolve_LineHasWords_ReturnsMappedCandidates()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(10, 20, 50, 30), "Hello", 0.9f, 0),
            new OcrWord(new PhysicalRect(70, 20, 60, 30), "World", 0.85f, 0),
            new OcrWord(new PhysicalRect(140, 20, 40, 30), "Foo", 0.95f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 200, 50), words);

        var result = _resolver.Resolve(line, lineIndex: 0);

        Assert.Equal(3, result.Count);
        Assert.Equal(0, result[0].LineIndex);
        Assert.Equal(0, result[0].WordIndex);
        Assert.Equal(new PhysicalRect(10, 20, 50, 30), result[0].Box);
        Assert.Equal("Hello", result[0].Text);
        Assert.Equal(0.9f, result[0].Confidence);

        Assert.Equal(1, result[1].WordIndex);
        Assert.Equal("World", result[1].Text);
        Assert.Equal(0.85f, result[1].Confidence);

        Assert.Equal(2, result[2].WordIndex);
        Assert.Equal("Foo", result[2].Text);
        Assert.Equal(0.95f, result[2].Confidence);
    }

    [Fact]
    public void Resolve_NoWords_SplitsTextBySpace()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 1000, 40),
            Array.Empty<OcrWord>(),
            text: "hello world");

        var result = _resolver.Resolve(line, lineIndex: 0);

        Assert.Equal(2, result.Count);

        Assert.Equal("hello", result[0].Text);
        Assert.Equal(new PhysicalRect(0, 0, 500, 40), result[0].Box);

        Assert.Equal("world", result[1].Text);
        Assert.Equal(new PhysicalRect(500, 0, 500, 40), result[1].Box);
    }

    [Fact]
    public void Resolve_NoWords_MultipleSpaces_TreatedAsSingleSeparator()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 1000, 40),
            Array.Empty<OcrWord>(),
            text: "hello   world");

        var result = _resolver.Resolve(line, lineIndex: 0);

        Assert.Equal(2, result.Count);
        Assert.Equal("hello", result[0].Text);
        Assert.Equal("world", result[1].Text);
    }

    [Fact]
    public void Resolve_EmptyText_ReturnsEmpty()
    {
        var line = new OcrLine(
            new PhysicalRect(0, 0, 100, 20),
            Array.Empty<OcrWord>(),
            text: "");

        var result = _resolver.Resolve(line, lineIndex: 0);

        Assert.Empty(result);
    }
}
