using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

/// <summary>
/// 拆分"跨空白合并行"：det 偶尔把左右两处相隔大片空白的文字合并成一个检测框
/// （如左页正文与右页正文之间隔着空白），导致行框/选区横跨空白区。
/// 依据词框之间的水平空隙，把这种行拆成多个独立行。
/// </summary>
public static class LineGapSplitter
{
    /// <summary>
    /// 词间空隙阈值 = max(行高 × GapFactor, MinGapPx, 平均字符宽 × CharWidthFactor)。
    /// 正常词间距约 0.2-0.5 倍行高；分栏栏间距/被图隔开的两段文字通常 ≥ 1 倍行高。
    /// GapFactor 取 1.5：行高项足以拆开栏间距，同时不误伤正常词距与字距；
    /// 字符宽项兼顾大字号/CJK 场景（字宽接近行高时按行高算会偏松）。
    /// </summary>
    private const double GapFactor = 1.5;
    private const int MinGapPx = 40;
    private const double CharWidthFactor = 4.0;

    // CJK 相邻时字宽项因子：中文排版字间空隙几乎为 0，超过 ~1 倍字宽的间隙
    // 基本都是分栏/跨区域断开；沿用拉丁文字的 4× 会让 CJK 行框横跨大片空白
    // （选区跨区域连通）。保留宽字距排版空间（≥ 1.25× 字宽才拆）。
    private const double CjkCharWidthFactor = 1.25;

    public static List<OcrLine> SplitLines(IReadOnlyList<OcrLine> lines)
    {
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
            result.AddRange(SplitLine(line));
        return result;
    }

    private static int ComputeGapThreshold(int lineHeight, List<OcrWord> cluster, OcrWord nextWord)
    {
        int byHeight = Math.Max((int)(lineHeight * GapFactor), MinGapPx);

        // 簇内平均字符宽：CJK/大字号下字宽接近行高，仅按行高会偏松
        double totalChars = 0;
        int totalWidth = 0;
        foreach (var w in cluster)
        {
            totalChars += Math.Max(1, w.Text.Length);
            totalWidth += w.Box.Width;
        }
        double avgCharWidth = totalWidth / Math.Max(1, totalChars);

        // 空隙两侧都是 CJK 时用紧因子（CJK 无词间空隙，大间隙 = 断开）；
        // 否则沿用宽因子保护拉丁文字的制表缩进/宽词距。
        double factor = ContainsCjk(cluster[^1].Text) && ContainsCjk(nextWord.Text)
            ? CjkCharWidthFactor
            : CharWidthFactor;
        int byCharWidth = (int)Math.Ceiling(avgCharWidth * factor);

        return Math.Max(byHeight, byCharWidth);
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;   // CJK 统一表意文字
            if (c >= 0x3400 && c <= 0x4DBF) return true;   // 扩展 A
            if (c >= 0x3000 && c <= 0x303F) return true;   // CJK 标点
            if (c >= 0xFF00 && c <= 0xFFEF) return true;   // 全角形
        }
        return false;
    }

    private static List<OcrLine> SplitLine(OcrLine line)
    {
        var words = line.Words;
        if (words.Count < 2)
            return new List<OcrLine> { line };

        var ordered = words.OrderBy(w => w.Box.X).ToList();
        var clusters = new List<List<OcrWord>> { new() { ordered[0] } };
        for (int i = 1; i < ordered.Count; i++)
        {
            int gap = ordered[i].Box.Left - clusters[^1][^1].Box.Right;
            if (gap > ComputeGapThreshold(line.Box.Height, clusters[^1], ordered[i]))
                clusters.Add(new List<OcrWord>());
            clusters[^1].Add(ordered[i]);
        }

        if (clusters.Count == 1)
            return new List<OcrLine> { line };

        var parts = new List<OcrLine>(clusters.Count);
        foreach (var cluster in clusters)
        {
            int left = cluster.Min(w => w.Box.Left);
            int top = cluster.Min(w => w.Box.Top);
            int right = cluster.Max(w => w.Box.Right);
            int bottom = cluster.Max(w => w.Box.Bottom);
            var box = new PhysicalRect(left, top, right - left, bottom - top);
            parts.Add(new OcrLine(box, cluster, string.Join(" ", cluster.Select(w => w.Text)), line.AngleDeg, line.Confidence));
        }
        return parts;
    }
}
