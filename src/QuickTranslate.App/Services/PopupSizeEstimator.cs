namespace QuickTranslate.App.Services;

/// <summary>
/// 弹窗尺寸估算：按文本内容（区分 CJK/ASCII 字宽）估算词/块弹窗的 DIP 尺寸，
/// 并钳制到显示器工作区的安全范围。替代旧的固定 320x150 / 440x480——
/// 短译文不再被拉伸出大片空白，长译文不再被裁剪。
/// </summary>
public static class PopupSizeEstimator
{
    // Word 弹窗基准（DIP，96dpi，与 XAML 紧凑化后的 Padding=12 保持一致）
    private const int WordMinW = 280;
    private const int WordMaxW = 480;
    private const double WordBodyFont = 14;
    private const double WordHeaderFont = 18;
    private const double WordHorizPadding = 24 + 6; // Border padding 12*2 + 冗余
    private const double WordVertChrome = 106;      // header（单行）+ margins + buttons + padding
    private const double WordLineHeight = 22;       // 与 XAML LineHeight 同步
    private const double WordHeaderLineHeight = 24; // 18px 标题换行时的行高
    private const int WordHeaderMaxLines = 3;       // 与 XAML WordHeader MaxHeight 一致

    // Block 弹窗基准（紧凑化后 Padding=11）
    private const int BlockMinW = 340;
    private const int BlockMaxW = 640;
    private const double BlockBodyFont = 14;
    private const double BlockVertChrome = 152;
    private const double BlockLineHeight = 22;      // 与 XAML LineHeight 同步

    /// <summary>估算字符串显示宽度（DIP）：CJK≈字号，ASCII/半角≈0.58*字号。忽略换行符。</summary>
    public static double EstimateTextWidth(string? text, double fontPx)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double w = 0;
        foreach (var c in text)
        {
            if (c is '\n' or '\r') continue;
            var isWide = c >= 0x2E80 && c <= 0x9FFF ||   // CJK 部首/汉字
                         c >= 0xAC00 && c <= 0xD7AF ||    // 谚文
                         c >= 0xF900 && c <= 0xFAFF ||    // CJK 兼容
                         c >= 0xFF01 && c <= 0xFF60 ||    // 全角标点
                         c >= 0x3000 && c <= 0x303F;      // CJK 标点
            w += isWide ? fontPx : fontPx * 0.58;
        }
        return w;
    }

    /// <summary>文本中最宽一行的显示宽度（含显式换行的多行内容按行取最大值）。</summary>
    private static double EstimateMaxLineWidth(string? text, double fontPx)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double max = 0;
        foreach (var line in text.Split('\n'))
        {
            var w = EstimateTextWidth(line.TrimEnd('\r'), fontPx);
            if (w > max) max = w;
        }
        return max;
    }

    /// <summary>显示行数：按显式换行分段，每段再按可用宽度折算换行后的行数（空段至少 1 行）。</summary>
    private static int CountDisplayLines(string? text, double fontPx, double innerWidth)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int total = 0;
        foreach (var line in text.Split('\n'))
        {
            var w = EstimateTextWidth(line.TrimEnd('\r'), fontPx);
            total += w <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(w / Math.Max(1, innerWidth)));
        }
        return Math.Max(1, total);
    }

    /// <summary>词弹窗尺寸：宽度取 min(单词宽度, 上限)，译文过长换行；高度随行数增长并钳制工作区。</summary>
    public static (int Width, int Height) EstimateWordPopupSize(
        string? header, string? body, double workAreaWidth, double workAreaHeight)
    {
        var maxW = (int)Math.Min(WordMaxW, Math.Max(WordMinW, workAreaWidth * 0.5));
        var minW = (int)Math.Min(WordMinW, maxW);

        var headerW = EstimateTextWidth(header, WordHeaderFont);
        // 词典释义等多行内容：宽度只看最宽一行，避免短行多行被拉满宽
        var bodyMaxLineW = EstimateMaxLineWidth(body, WordBodyFont);

        // 宽度：能让最宽一行放下就放（封顶 maxW）；否则加宽到能 2~3 行放下
        int width;
        if (bodyMaxLineW + WordHorizPadding <= minW)
        {
            width = minW;
        }
        else if (bodyMaxLineW + WordHorizPadding <= maxW)
        {
            // 单行可容纳：宽度跟随内容 + 少量余量
            width = (int)Math.Ceiling(Math.Max(headerW, bodyMaxLineW) + WordHorizPadding + 12);
            width = Math.Clamp(width, minW, maxW);
        }
        else
        {
            width = maxW;
        }

        var effInner = width - WordHorizPadding;
        var lines = CountDisplayLines(body, WordBodyFont, effInner);
        var height = (int)Math.Ceiling(WordVertChrome + lines * WordLineHeight);

        // 选中文本是一整句时标题会换行（最多 WordHeaderMaxLines 行）：首行已含在 VertChrome 里，补算额外行
        var headerLines = Math.Min(WordHeaderMaxLines, CountDisplayLines(header, WordHeaderFont, effInner));
        height += (int)Math.Ceiling((headerLines - 1) * WordHeaderLineHeight);

        var maxH = (int)Math.Max(140, workAreaHeight * 0.45);
        height = Math.Clamp(height, 110, maxH);
        return (width, height);
    }

    /// <summary>块弹窗尺寸：宽度 340~640（工作区 60%），高度按行数估算并钳制工作区 60%。</summary>
    public static (int Width, int Height) EstimateBlockPopupSize(
        string? sourceText, string? translationText, double workAreaWidth, double workAreaHeight)
    {
        var maxW = (int)Math.Min(BlockMaxW, Math.Max(BlockMinW, workAreaWidth * 0.6));
        var width = maxW;

        // 源文（小号灰字，最多 3 行）+ 译文主体；两者都可能含换行，按段累计行数
        var srcLines = Math.Min(3, CountDisplayLines(sourceText, 11.5, width - 34));
        if (string.IsNullOrWhiteSpace(sourceText)) srcLines = 0;
        var bodyLines = CountDisplayLines(translationText, BlockBodyFont, width - 34);

        var height = (int)Math.Ceiling(BlockVertChrome + srcLines * 17 + bodyLines * BlockLineHeight);
        var maxH = (int)Math.Max(240, workAreaHeight * 0.6);
        height = Math.Clamp(height, 200, maxH);
        return (width, height);
    }
}
