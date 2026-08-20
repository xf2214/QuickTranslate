using QuickTranslate.App.Services;
using Xunit;

namespace QuickTranslate.Tests.App;

public class PopupSizeEstimatorTests
{
    [Fact]
    public void WordPopup_ShortTranslation_MinWidthNotStretched()
    {
        var (w, h) = PopupSizeEstimator.EstimateWordPopupSize("hello", "你好", 1920, 1080);
        Assert.InRange(w, 280, 480);
        Assert.InRange(h, 110, 1080 * 0.45);
        // 短译文：高度接近最小值
        Assert.True(h <= 160, $"short text should be compact, got h={h}");
    }

    [Fact]
    public void WordPopup_LongTranslation_GrowsHeight_AndClampsToWorkArea()
    {
        var longText = new string('译', 200);
        var (w, h) = PopupSizeEstimator.EstimateWordPopupSize("word", longText, 1920, 1080);
        Assert.Equal(480, w); // 顶到最大宽
        Assert.True(h > 160, "long text should grow height");

        // 极小工作区（如 400x300）：高度必须被钳制（估算器下限 140）
        var (_, h2) = PopupSizeEstimator.EstimateWordPopupSize("word", longText, 400, 300);
        Assert.True(h2 <= 140, $"clamped height, got {h2}");
    }

    [Fact]
    public void WordPopup_WideAscii_GrowsWidthWithinBounds()
    {
        var medium = "translation result text sample"; // 约 30 ASCII 字符
        var (w, _) = PopupSizeEstimator.EstimateWordPopupSize("word", medium, 1920, 1080);
        Assert.InRange(w, 280, 480);
    }

    [Fact]
    public void BlockPopup_WithinBounds_AndScalesWithText()
    {
        var (w1, h1) = PopupSizeEstimator.EstimateBlockPopupSize("Hello world", "你好世界", 1920, 1080);
        Assert.InRange(w1, 340, 640);
        Assert.InRange(h1, 200, 1080 * 0.6);

        var longSrc = new string('a', 2000);
        var longDst = new string('译', 500);
        var (_, h2) = PopupSizeEstimator.EstimateBlockPopupSize(longSrc, longDst, 1920, 1080);
        Assert.True(h2 > h1, "longer text should need taller popup");
    }

    [Fact]
    public void Cjk_CharWidth_Greater_Than_Ascii()
    {
        Assert.True(PopupSizeEstimator.EstimateTextWidth("中文中文", 14) >
                    PopupSizeEstimator.EstimateTextWidth("abcd", 14));
    }

    [Fact]
    public void WordPopup_MultilineDictionary_ShortLines_KeepsNarrow_GrowsHeight()
    {
        // 词典释义多行但每行都短：宽度不应被拉满，高度随行数增长
        var multiline = "[əˈpɒl]\nn. 苹果\n[医] 苹果";
        var (w, h) = PopupSizeEstimator.EstimateWordPopupSize("apple", multiline, 1920, 1080);

        Assert.True(w < 480, $"短多行内容不应顶到最大宽，got {w}");

        var (_, hSingle) = PopupSizeEstimator.EstimateWordPopupSize("apple", "n. 苹果", 1920, 1080);
        Assert.True(h > hSingle, "行数更多应更高");
    }

    [Fact]
    public void WordPopup_NewlineChar_NotCountedAsWidth()
    {
        var withNewline = "ab\ncd";
        Assert.Equal(PopupSizeEstimator.EstimateTextWidth("abcd", 14),
                     PopupSizeEstimator.EstimateTextWidth(withNewline, 14), 3);
    }
}
