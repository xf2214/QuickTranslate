using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class OcrLayoutResultTests
{
    [Fact]
    public void OcrLine_DefaultText_JoinsWordsWithSpace()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 10, 10), "Hello", 0.9f, 0),
            new OcrWord(new PhysicalRect(10, 0, 10, 10), "World", 0.9f, 0)
        };

        var line = new OcrLine(new PhysicalRect(0, 0, 100, 20), words);

        Assert.Equal("Hello World", line.Text);
    }

    [Fact]
    public void OcrLine_ExplicitText_OverridesJoining()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 10, 10), "A", 0.9f, 0),
            new OcrWord(new PhysicalRect(10, 0, 10, 10), "B", 0.9f, 0)
        };

        var line = new OcrLine(new PhysicalRect(0, 0, 100, 20), words, text: "Custom Text");

        Assert.Equal("Custom Text", line.Text);
    }

    [Fact]
    public void OcrLine_Confidence_DefaultsNull_AndRoundTrips()
    {
        var words = new[] { new OcrWord(new PhysicalRect(0, 0, 10, 10), "x", 0.9f, 0) };

        var noConf = new OcrLine(new PhysicalRect(0, 0, 10, 10), words);
        Assert.Null(noConf.Confidence);

        var withConf = new OcrLine(new PhysicalRect(0, 0, 10, 10), words, confidence: 0.82f);
        Assert.Equal(0.82f, withConf.Confidence);
    }

    [Fact]
    public void OcrTimings_Total_SumsAllSegments()
    {
        var preprocess = TimeSpan.FromMilliseconds(10);
        var detector = TimeSpan.FromMilliseconds(20);
        var classifier = TimeSpan.FromMilliseconds(5);
        var recognizer = TimeSpan.FromMilliseconds(30);
        var postprocess = TimeSpan.FromMilliseconds(15);

        var timings = new OcrTimings(preprocess, detector, classifier, recognizer, postprocess);

        var expected = preprocess + detector + classifier + recognizer + postprocess;
        Assert.Equal(expected, timings.Total);
        Assert.Equal(80, timings.Total.TotalMilliseconds);
    }

    [Fact]
    public void OcrTimings_ZeroSegments_TotalIsZero()
    {
        var timings = new OcrTimings(
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, timings.Total);
    }

    [Fact]
    public void OcrLayoutResult_LineCount_MatchesLinesCount()
    {
        var lines = new[]
        {
            new OcrLine(
                new PhysicalRect(0, 0, 100, 20),
                new[] { new OcrWord(new PhysicalRect(0, 0, 50, 20), "Line1", 0.9f, 0) }),
            new OcrLine(
                new PhysicalRect(0, 20, 100, 20),
                new[] { new OcrWord(new PhysicalRect(0, 20, 50, 20), "Line2", 0.9f, 1) }),
            new OcrLine(
                new PhysicalRect(0, 40, 100, 20),
                new[] { new OcrWord(new PhysicalRect(0, 40, 50, 20), "Line3", 0.9f, 2) })
        };

        var timings = new OcrTimings(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(5));

        var result = new OcrLayoutResult(
            new PhysicalRect(0, 0, 800, 600),
            lines,
            timings,
            DateTimeOffset.Now);

        Assert.Equal(3, result.LineCount);
        Assert.Equal(lines.Length, result.LineCount);
    }

    [Fact]
    public void OcrLine_SingleWord_TextMatchesWord()
    {
        var word = new OcrWord(new PhysicalRect(0, 0, 50, 20), "SingleWord", 0.99f, 0);
        var line = new OcrLine(new PhysicalRect(0, 0, 100, 20), new[] { word });

        Assert.Equal("SingleWord", line.Text);
        Assert.Single(line.Words);
    }

    [Fact]
    public void OcrLayoutResult_WithOptionalParams_PreservesValues()
    {
        var lines = new List<OcrLine>();
        var timings = new OcrTimings(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(5));

        var captureTime = DateTimeOffset.Now;
        var result = new OcrLayoutResult(
            new PhysicalRect(10, 20, 30, 40),
            lines,
            timings,
            captureTime,
            DpiX: 144,
            DpiY: 144,
            EngineName: "TestEngine",
            FromCache: true);

        Assert.Equal(new PhysicalRect(10, 20, 30, 40), result.CaptureRegion);
        Assert.Equal(0, result.LineCount);
        Assert.Equal(captureTime, result.CaptureTime);
        Assert.True(result.DpiX.HasValue);
        Assert.Equal(144u, result.DpiX.Value);
        Assert.True(result.DpiY.HasValue);
        Assert.Equal(144u, result.DpiY.Value);
        Assert.Equal("TestEngine", result.EngineName);
        Assert.True(result.FromCache);
    }
}
