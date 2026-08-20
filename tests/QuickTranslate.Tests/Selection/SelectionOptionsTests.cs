using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Selection;

public class SelectionOptionsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var opts = SelectionOptions.Default;

        Assert.Equal(36, opts.MaxAnchorDistanceBase);
        Assert.Equal(1.2f, opts.MaxAnchorDistanceRowHeightFactor);
        Assert.Equal(4, opts.MinWordWidth);
        Assert.Equal(3, opts.MinWordHeight);
        Assert.Equal(0.3f, opts.ConfidenceFloor);
        Assert.Equal(30, opts.MaxCandidatesPerLine);
        Assert.Equal(1.3f, opts.MaxWordWidthPerCharHeightFactor);
        Assert.Equal(2.0f, opts.BlockMaxAnchorDistanceFactor);
        Assert.Equal(2.5f, opts.BlockMaxWidthVsMedianFactor);
        Assert.Equal(1.8f, opts.BlockParagraphGapVsMedianFactor);
        Assert.Equal(4, opts.BlockParagraphGapMinPx);
        Assert.Equal(0.2f, opts.BlockShortTailRightMarginFactor);
        Assert.Equal(0.12f, opts.BlockFullWidthRightMarginFactor);
        Assert.Equal(1.0f, opts.BlockFirstLineIndentFactor);
    }

    [Fact]
    public void ComputeEffectiveMax_LineHeight16_Returns36()
    {
        var opts = new SelectionOptions();
        Assert.Equal(36, opts.ComputeEffectiveMax(16));
    }

    [Fact]
    public void ComputeEffectiveMax_LineHeight40_Returns48()
    {
        var opts = new SelectionOptions();
        Assert.Equal(48, opts.ComputeEffectiveMax(40));
    }

    [Fact]
    public void ComputeEffectiveMax_LineHeight80_Returns96()
    {
        var opts = new SelectionOptions();
        Assert.Equal(96, opts.ComputeEffectiveMax(80));
    }
}
