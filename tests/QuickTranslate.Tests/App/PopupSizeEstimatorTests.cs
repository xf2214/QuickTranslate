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

    // —— Block 宽度自适应回归 ——

    [Fact]
    public void BlockPopup_ShortContent_ClampsToMinWidth()
    {
        // 短译文不再拉满宽：自然宽远小于 minW 时应取下限 340
        var (w, h) = PopupSizeEstimator.EstimateBlockPopupSize("Hello world", "你好世界", 1920, 1080);
        Assert.Equal(340, w);
        Assert.InRange(h, 200, 1080 * 0.6);
    }

    [Fact]
    public void BlockPopup_MediumContent_WidthFollowsContent()
    {
        // 中等长度（50~70 ASCII）应落在 (340,640) 区间且明显宽于短文本
        var (wShort, _) = PopupSizeEstimator.EstimateBlockPopupSize("Hello world", "你好世界", 1920, 1080);
        var mediumText = new string('a', 60);
        var (wMed, hMed) = PopupSizeEstimator.EstimateBlockPopupSize("Hello world", mediumText, 1920, 1080);
        Assert.True(wMed > 340 && wMed < 640, $"medium width should be adaptive, got {wMed}");
        Assert.True(wMed > wShort, $"medium {wMed} should be wider than short {wShort}");
        Assert.InRange(hMed, 200, 1080 * 0.6);
    }

    [Fact]
    public void BlockPopup_VeryLongContent_ClampsToMaxWidth()
    {
        // 超长文本顶到上限 640
        var longText = new string('a', 2000);
        var (w, h) = PopupSizeEstimator.EstimateBlockPopupSize(longText, longText, 1920, 1080);
        Assert.Equal(640, w);
        Assert.InRange(h, 200, 1080 * 0.6);
    }

    [Fact]
    public void BlockPopup_CjkWiderThanAscii_SameCharCount()
    {
        // 同字符数的中文比等量 ASCII 需要更宽（CJK 字宽更大）
        var cjkText = new string('中', 40);
        var asciiText = new string('a', 40);
        var (wCjk, _) = PopupSizeEstimator.EstimateBlockPopupSize("src", cjkText, 1920, 1080);
        var (wAscii, _) = PopupSizeEstimator.EstimateBlockPopupSize("src", asciiText, 1920, 1080);
        Assert.True(wCjk >= wAscii, $"CJK width {wCjk} should be >= ascii {wAscii}");
    }

    [Fact]
    public void BlockPopup_TinyWorkArea_ClampsWidthAndHeight()
    {
        // 极小工作区：宽度被钳制到 340，高度仍被钳制到 [200,240]
        var longText = new string('a', 2000);
        var (w, h) = PopupSizeEstimator.EstimateBlockPopupSize(longText, longText, 400, 300);
        Assert.Equal(340, w);
        Assert.InRange(h, 200, 240);
    }

    [Fact]
    public void BlockPopup_NullOrEmptySource_WithinBounds()
    {
        // 错误路径：source 为 null / 空串，尺寸仍在合法界内
        var (w1, h1) = PopupSizeEstimator.EstimateBlockPopupSize(null, "你好", 1920, 1080);
        Assert.InRange(w1, 340, 640);
        Assert.InRange(h1, 200, 1080 * 0.6);

        var (w2, h2) = PopupSizeEstimator.EstimateBlockPopupSize("", "Hi", 1920, 1080);
        Assert.InRange(w2, 340, 640);
        Assert.InRange(h2, 200, 1080 * 0.6);

        var (w3, h3) = PopupSizeEstimator.EstimateBlockPopupSize(null, null, 1920, 1080);
        Assert.InRange(w3, 340, 640);
        Assert.InRange(h3, 200, 1080 * 0.6);
    }

    [Fact]
    public void BlockPopup_HeightGrowsWithLongerTranslation()
    {
        // 高度行为回归：长译文高度 > 短译文高度
        var (_, hShort) = PopupSizeEstimator.EstimateBlockPopupSize("Hello world", "你好", 1920, 1080);
        var longText = new string('译', 200);
        var (_, hLong) = PopupSizeEstimator.EstimateBlockPopupSize("Hello world", longText, 1920, 1080);
        Assert.True(hLong > hShort, $"long height {hLong} should be > short {hShort}");
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

    [Fact]
    public void WordPopup_LongSentenceHeader_WrapsAndGrowsHeight()
    {
        // 选中文本是一整句：标题换行后高度应大于单词标题（上限 3 行，不无限增长）
        var sentence = "this is a rather long selected sentence for translation";
        var (_, hSentence) = PopupSizeEstimator.EstimateWordPopupSize(sentence, "这是一个很长的句子译文", 1920, 1080);
        var (_, hWord) = PopupSizeEstimator.EstimateWordPopupSize("word", "这是一个很长的句子译文", 1920, 1080);

        Assert.True(hSentence > hWord, $"长句标题应更高: {hSentence} vs {hWord}");

        var veryLong = string.Join(" ", Enumerable.Repeat(sentence, 4));
        var (_, hVeryLong) = PopupSizeEstimator.EstimateWordPopupSize(veryLong, "译文", 1920, 1080);
        Assert.True(hVeryLong <= hSentence + 1, "标题最多补算 3 行，不应无限增高");
    }
}
