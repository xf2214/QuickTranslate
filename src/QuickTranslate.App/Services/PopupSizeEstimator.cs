namespace QuickTranslate.App.Services;

/// <summary>
/// 弹窗尺寸估算：按文本内容（区分 CJK/ASCII 字宽）估算词/块弹窗的 DIP 尺寸，
/// 并钳制到显示器工作区的安全范围。替代旧的固定 320x150 / 440x480——
/// 短译文不再被拉伸出大片空白，长译文不再被裁剪。
/// </summary>
public static class PopupSizeEstimator
{
    // Word 弹窗基准（DIP，96dpi）
    private const int WordMinW = 300;
    private const int WordMaxW = 520;
    private const double WordBodyFont = 14;
    private const double WordHeaderFont = 18;
    private const double WordHorizPadding = 28 + 6; // Border padding 14*2 + 冗余
    private const double WordVertChrome = 118;      // header + margins + buttons + padding

    // Block 弹窗基准
    private const int BlockMinW = 360;
    private const int BlockMaxW = 720;
    private const double BlockBodyFont = 14;
    private const double BlockVertChrome = 170;

    /// <summary>估算字符串显示宽度（DIP）：CJK≈字号，ASCII/半角≈0.58*字号。</summary>
    public static double EstimateTextWidth(string? text, double fontPx)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double w = 0;
        foreach (var c in text)
        {
            var isWide = c >= 0x2E80 && c <= 0x9FFF ||   // CJK 部首/汉字
                         c >= 0xAC00 && c <= 0xD7AF ||    // 谚文
                         c >= 0xF900 && c <= 0xFAFF ||    // CJK 兼容
                         c >= 0xFF01 && c <= 0xFF60 ||    // 全角标点
                         c >= 0x3000 && c <= 0x303F;      // CJK 标点
            w += isWide ? fontPx : fontPx * 0.58;
        }
        return w;
    }

    /// <summary>词弹窗尺寸：宽度取 min(单词宽度, 上限)，译文过长换行；高度随行数增长并钳制工作区。</summary>
    public static (int Width, int Height) EstimateWordPopupSize(
        string? header, string? body, double workAreaWidth, double workAreaHeight)
    {
        var maxW = (int)Math.Min(WordMaxW, Math.Max(WordMinW, workAreaWidth * 0.5));
        var minW = (int)Math.Min(WordMinW, maxW);

        var headerW = EstimateTextWidth(header, WordHeaderFont);
        var innerW = Math.Max(60.0, maxW - WordHorizPadding);
        var bodyW = EstimateTextWidth(body, WordBodyFont);

        // 宽度：能让译文单行放下就放（封顶 maxW）；否则加宽到能 2~3 行放下
        int width;
        if (bodyW + WordHorizPadding <= minW)
        {
            width = minW;
        }
        else if (bodyW + WordHorizPadding <= maxW)
        {
            // 单行可容纳：宽度跟随内容 + 少量余量
            width = (int)Math.Ceiling(Math.Max(headerW, bodyW) + WordHorizPadding + 12);
            width = Math.Clamp(width, minW, maxW);
        }
        else
        {
            width = maxW;
        }

        var effInner = width - WordHorizPadding;
        var lines = Math.Max(1, (int)Math.Ceiling(bodyW / Math.Max(1, effInner)));
        var height = (int)Math.Ceiling(WordVertChrome + lines * (WordBodyFont + 8));

        var maxH = (int)Math.Max(140, workAreaHeight * 0.45);
        height = Math.Clamp(height, 110, maxH);
        return (width, height);
    }

    /// <summary>块弹窗尺寸：宽度 360~720（工作区 60%），高度按行数估算并钳制工作区 60%。</summary>
    public static (int Width, int Height) EstimateBlockPopupSize(
        string? sourceText, string? translationText, double workAreaWidth, double workAreaHeight)
    {
        var maxW = (int)Math.Min(BlockMaxW, Math.Max(BlockMinW, workAreaWidth * 0.6));
        var width = maxW;

        // 源文（小号灰字，最多 3 行）+ 译文主体
        var srcW = EstimateTextWidth(sourceText, 11.5);
        var srcLines = Math.Min(3, Math.Max(sourceText?.Length > 0 ? 1 : 0, (int)Math.Ceiling(srcW / Math.Max(1, width - 36))));
        var bodyW = EstimateTextWidth(translationText, BlockBodyFont);
        var bodyLines = Math.Max(1, (int)Math.Ceiling(bodyW / Math.Max(1, width - 36)));

        var height = (int)Math.Ceiling(BlockVertChrome + srcLines * 17 + bodyLines * (BlockBodyFont + 8));
        var maxH = (int)Math.Max(240, workAreaHeight * 0.6);
        height = Math.Clamp(height, 200, maxH);
        return (width, height);
    }
}
