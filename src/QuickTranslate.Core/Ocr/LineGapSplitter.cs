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
    /// 正常词间距约 0.2-0.5 倍行高，制表缩进最多 2-3 倍；
    /// 字符宽项兼顾大字号/CJK 场景（字宽接近行高时按行高算会偏松）。
    /// </summary>
    private const double GapFactor = 2.5;
    private const int MinGapPx = 40;
    private const double CharWidthFactor = 4.0;

    public static List<OcrLine> SplitLines(IReadOnlyList<OcrLine> lines)
    {
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
            result.AddRange(SplitLine(line));
        return result;
    }

    private static int ComputeGapThreshold(int lineHeight, List<OcrWord> cluster)
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
        int byCharWidth = (int)Math.Ceiling(totalWidth / totalChars * CharWidthFactor);

        return Math.Max(byHeight, byCharWidth);
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
            if (gap > ComputeGapThreshold(line.Box.Height, clusters[^1]))
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
            parts.Add(new OcrLine(box, cluster, string.Join(" ", cluster.Select(w => w.Text)), line.AngleDeg));
        }
        return parts;
    }
}
