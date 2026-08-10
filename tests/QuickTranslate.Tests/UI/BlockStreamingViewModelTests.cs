using Xunit;

namespace QuickTranslate.Tests.UI;

public class BlockStreamingViewModelTests
{
    private class StreamBuffer
    {
        public string FullText { get; private set; } = "";
        public int ScrollToEndCallCount { get; private set; }

        public void AppendChunk(string delta)
        {
            FullText += delta;
            ScrollToEndCallCount++;
        }
    }

    [Fact]
    public void AppendChunk_First_IncreasesFullText_AndScrollToEnd_Called()
    {
        var buffer = new StreamBuffer();
        buffer.AppendChunk("A");
        Assert.Equal("A", buffer.FullText);
        Assert.Equal(1, buffer.ScrollToEndCallCount);
    }

    [Fact]
    public void AppendChunk_Second_Concatenates_AndScrollToEnd_Called_OnLast()
    {
        var buffer = new StreamBuffer();
        buffer.AppendChunk("A");
        Assert.Equal("A", buffer.FullText);

        buffer.AppendChunk("B");
        Assert.Equal("AB", buffer.FullText);
        Assert.Equal(2, buffer.ScrollToEndCallCount);
    }

    [Fact]
    public void AppendChunk_Multiple_AccumulatesCorrectly()
    {
        var buffer = new StreamBuffer();
        buffer.AppendChunk("Hello ");
        buffer.AppendChunk("World");
        buffer.AppendChunk("!");
        Assert.Equal("Hello World!", buffer.FullText);
        Assert.Equal(3, buffer.ScrollToEndCallCount);
    }
}
