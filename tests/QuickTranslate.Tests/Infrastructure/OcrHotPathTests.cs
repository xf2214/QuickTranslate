using QuickTranslate.Core.Geometry;
using QuickTranslate.Infrastructure.Ocr;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;
using Xunit.Abstractions;

namespace QuickTranslate.Tests.Infrastructure;

public class OcrHotPathTests
{
    private readonly ITestOutputHelper _output;

    public OcrHotPathTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FocusBand_KeepsAllTwelveParagraphLines()
    {
        // A paragraph longer than eight lines must not silently lose its ends.
        var boxes = Enumerable.Range(0, 12)
            .Select(i => new PhysicalRect(10, 5 + i * 20, 300, 16)).ToList();

        var selected = OcrRegionSelector.FilterByFocusBand(
            boxes, new PhysicalRect(100, 200, 400, 300), new PhysicalRect(110, 200, 2, 260));

        Assert.Equal(12, selected.Count);
        Assert.Equal(5, selected[0].Y);
        Assert.Equal(225, selected[^1].Y);
    }

    [Fact]
    public void FocusBand_UsesScreenOffsetAndExcludesOnlyTouchingEdges()
    {
        var boxes = new List<PhysicalRect>
        {
            new(0, 0, 30, 10),   // ends exactly at band top
            new(0, 9, 30, 10),   // overlaps band top by one pixel
            new(0, 29, 30, 10),  // overlaps band bottom by one pixel
            new(0, 30, 30, 10),  // starts exactly at band bottom
        };

        var selected = OcrRegionSelector.FilterByFocusBand(
            boxes, new PhysicalRect(-500, -200, 100, 100), new PhysicalRect(0, -190, 2, 20));

        Assert.Equal(new[] { boxes[1], boxes[2] }, selected);
    }

    [Fact]
    public void FocusBand_Absent_ReturnsAllBoxesInDetectorOrder()
    {
        var boxes = new List<PhysicalRect> { new(1, 40, 10, 8), new(1, 10, 10, 8) };

        var selected = OcrRegionSelector.FilterByFocusBand(boxes, new PhysicalRect(0, 0, 100, 100), null);

        Assert.Equal(boxes, selected);
    }

    [Fact]
    public void Decode_SpanSlice_PreservesBlanksRepeatsSpaceAndMeanConfidence()
    {
        // Labels: blank=0, a=1, b=2, space=3. Blank separates the two a's.
        float[] buffer =
        [
            99, 99, 99, 99,
            .05f, .85f, .05f, .05f,
            .05f, .85f, .05f, .05f,
            .85f, .05f, .05f, .05f,
            .05f, .85f, .05f, .05f,
            .05f, .05f, .05f, .85f,
            .1f, .1f, .7f, .1f,
            99, 99, 99, 99,
        ];

        var result = CtcGreedyDecoder.Decode(buffer.AsSpan(4, 24), 6, 4, ["", "a", "b", " "]);

        Assert.Equal("aa b", result.Text);
        Assert.Equal(.8125f, result.Confidence, 5);
    }

    [Fact]
    public void Decode_Logits_UsesStableSoftmaxConfidence()
    {
        var result = CtcGreedyDecoder.Decode([1000, 1002, 1000], 1, 3, ["", "a", " "]);

        Assert.Equal("a", result.Text);
        Assert.Equal(.786986f, result.Confidence, 5);
    }

    [Fact]
    public void Decode_AllBlanks_ProducesEmptyTextAndZeroConfidence()
    {
        var result = CtcGreedyDecoder.Decode([.9f, .05f, .05f], 1, 3, ["", "a", " "]);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.Confidence);
    }

    [Theory]
    [InlineData(-1, 3, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(2, 3, 3)]
    [InlineData(1, 3, 4)]
    [InlineData(int.MaxValue, 3, 0)]
    public void Decode_InvalidTensorShape_IsRejected(int steps, int classes, int bufferLength)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            CtcGreedyDecoder.Decode(new float[bufferLength], steps, classes, ["", "a", " "]));
    }

    [Fact]
    public void Decode_MismatchedDictionary_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            CtcGreedyDecoder.Decode([.1f, .2f, .7f], 1, 3, ["", "a"]));
    }

    [Fact]
    public void Decode_LargeOutput_DoesNotAllocateTensorSizedCopy()
    {
        const int classes = 18710;
        const int steps = 160;
        var probabilities = new float[classes * steps];
        var dictionary = new string[classes];
        Array.Fill(dictionary, "a");
        dictionary[0] = string.Empty;
        for (int t = 0; t < steps; t++) probabilities[t * classes] = 1;
        _ = CtcGreedyDecoder.Decode(probabilities, steps, classes, dictionary);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = CtcGreedyDecoder.Decode(probabilities, steps, classes, dictionary);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"Span decoding allocated {allocated} managed bytes.");
        Assert.Equal(string.Empty, result.Text);
        Assert.True(allocated < 4096, $"Decoder allocated {allocated} bytes for an existing 12 MB tensor.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecodeTensor_AcceptsSingleBatchOrUnbatchedCtcOutput(bool batchDimension)
    {
        var tensor = new DenseTensor<float>(new[] { .1f, .8f, .1f }, batchDimension ? [1, 1, 3] : [1, 3]);

        var result = CtcGreedyDecoder.DecodeTensor(tensor, ["", "a", " "]);

        Assert.Equal("a", result.Text);
        Assert.Equal(.8f, result.Confidence, 5);
    }

    [Fact]
    public void DecodeTensor_BatchGreaterThanOne_IsRejected()
    {
        var tensor = new DenseTensor<float>(new float[6], [2, 1, 3]);

        Assert.Throws<ArgumentException>(() => CtcGreedyDecoder.DecodeTensor(tensor, ["", "a", " "]));
    }

    [Fact]
    public void DecodeTensor_DenseOutput_DoesNotAllocateTensorSizedCopy()
    {
        const int classes = 18710;
        var tensor = new DenseTensor<float>([1, 160, classes]);
        var dictionary = new string[classes];
        Array.Fill(dictionary, "a");
        dictionary[0] = string.Empty;
        for (int t = 0; t < 160; t++) tensor[0, t, 0] = 1;
        _ = CtcGreedyDecoder.DecodeTensor(tensor, dictionary);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = CtcGreedyDecoder.DecodeTensor(tensor, dictionary);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"DenseTensor decoding allocated {allocated} managed bytes.");
        Assert.Equal(string.Empty, result.Text);
        Assert.True(allocated < 4096, $"Tensor decoding allocated {allocated} bytes for an existing 12 MB output.");
    }
}
