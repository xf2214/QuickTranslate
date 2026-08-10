using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Infrastructure.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class MockOcrEngineTests
{
    private static ScreenFrame CreateTestFrame(int width = 800, int height = 600)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var region = new PhysicalRect(0, 0, width, height);
        return new ScreenFrame(bmp, region, MonitorId.Empty);
    }

    [Fact]
    public async Task WarmUpAsync_CompletesImmediately()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        var warmupTask = engine.WarmUpAsync();
        Assert.True(warmupTask.IsCompleted);

        await warmupTask;
    }

    [Fact]
    public async Task RecognizeAsync_ReturnsFiveLines()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        using var frame = CreateTestFrame();
        var result = await engine.RecognizeAsync(frame);

        Assert.NotNull(result);
        Assert.Equal(5, result.Lines.Count);
        Assert.Equal(5, result.LineCount);
    }

    [Fact]
    public async Task RecognizeAsync_AllLinesHaveNonEmptyText()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        using var frame = CreateTestFrame();
        var result = await engine.RecognizeAsync(frame);

        foreach (var line in result.Lines)
        {
            Assert.False(string.IsNullOrWhiteSpace(line.Text), $"Line text should not be empty. Got: '{line.Text}'");
        }
    }

    [Fact]
    public void EngineName_IsMockOcr()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        Assert.Equal("MockOcr", engine.EngineName);
    }

    [Fact]
    public void IsAvailable_IsFalse()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        Assert.False(engine.IsAvailable);
    }

    [Fact]
    public async Task RecognizeAsync_WritesStructuredLogEntry()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        using var frame = CreateTestFrame();
        await engine.RecognizeAsync(frame);

        var ocrLog = entries.FirstOrDefault(e => e.Message.Contains("OCR finished"));
        Assert.NotNull(ocrLog);
        Assert.Equal(LogLevel.Information, ocrLog.Level);

        Assert.Contains("Engine=MockOcr", ocrLog.Message);
        Assert.Contains("Lines=5", ocrLog.Message);

        var stateDict = ocrLog.State.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.True(stateDict.ContainsKey("EngineName"), "Log state should contain EngineName");
        Assert.Equal("MockOcr", stateDict["EngineName"]);

        Assert.True(stateDict.ContainsKey("LineCount"), "Log state should contain LineCount");
        Assert.Equal(5, stateDict["LineCount"]);

        Assert.True(stateDict.ContainsKey("PreprocessMs"), "Log state should contain PreprocessMs");
        Assert.True(Convert.ToDouble(stateDict["PreprocessMs"]) >= 0, "PreprocessMs should be >= 0");

        Assert.True(stateDict.ContainsKey("DetectorMs"), "Log state should contain DetectorMs");
        Assert.True(Convert.ToDouble(stateDict["DetectorMs"]) >= 0, "DetectorMs should be >= 0");

        Assert.True(stateDict.ContainsKey("ClassifierMs"), "Log state should contain ClassifierMs");
        Assert.True(Convert.ToDouble(stateDict["ClassifierMs"]) >= 0, "ClassifierMs should be >= 0");

        Assert.True(stateDict.ContainsKey("RecognizerMs"), "Log state should contain RecognizerMs");
        Assert.True(Convert.ToDouble(stateDict["RecognizerMs"]) >= 0, "RecognizerMs should be >= 0");

        Assert.True(stateDict.ContainsKey("PostprocessMs"), "Log state should contain PostprocessMs");
        Assert.True(Convert.ToDouble(stateDict["PostprocessMs"]) >= 0, "PostprocessMs should be >= 0");
    }

    [Fact]
    public async Task RecognizeAsync_LinesHaveWords()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        using var frame = CreateTestFrame();
        var result = await engine.RecognizeAsync(frame);

        foreach (var line in result.Lines)
        {
            Assert.NotEmpty(line.Words);
        }
    }

    [Fact]
    public async Task RecognizeAsync_TimingsTotalIsNonNegative()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        using var frame = CreateTestFrame();
        var result = await engine.RecognizeAsync(frame);

        Assert.True(result.Timings.Total >= TimeSpan.Zero);
        Assert.True(result.Timings.Preprocess >= TimeSpan.Zero);
        Assert.True(result.Timings.Detector >= TimeSpan.Zero);
        Assert.True(result.Timings.Classifier >= TimeSpan.Zero);
        Assert.True(result.Timings.Recognizer >= TimeSpan.Zero);
        Assert.True(result.Timings.Postprocess >= TimeSpan.Zero);
    }
}
