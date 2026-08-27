using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Selection;

public class DefaultBlockSelector : IBlockSelector
{
    public BlockSelectionResult SelectBlock(OcrLayoutResult ocr, PhysicalPoint anchor, SelectionOptions? opts = null)
    {
        opts ??= SelectionOptions.Default;

        if (ocr.Lines.Count == 0)
        {
            return new BlockSelectionResult(
                BlockText: null,
                UnionBox: PhysicalRect.Empty,
                SelectedLines: Array.Empty<OcrLine>(),
                Kind: SelectionKind.Block,
                OperationId: Guid.NewGuid(),
                NoBlockFound: true);
        }

        OcrLine anchorLine = FindAnchorLine(ocr.Lines, anchor, out bool anchorContained, out double anchorDistance);

        // 光标不在任何行内且离最近行太远 → 视为无目标块，
        // 避免光标在空白区时把不相近的段落误当目标。
        if (!anchorContained)
        {
            double maxDist = Math.Max(
                opts.MaxAnchorDistanceBase,
                anchorLine.Box.Height * opts.BlockMaxAnchorDistanceFactor);
            if (anchorDistance > maxDist)
            {
                return new BlockSelectionResult(
                    BlockText: null,
                    UnionBox: PhysicalRect.Empty,
                    SelectedLines: Array.Empty<OcrLine>(),
                    Kind: SelectionKind.Block,
                    OperationId: Guid.NewGuid(),
                    NoBlockFound: true);
            }
        }

        int anchorLineIndex = -1;
        for (int idx = 0; idx < ocr.Lines.Count; idx++)
        {
            if (ReferenceEquals(ocr.Lines[idx], anchorLine) || ocr.Lines[idx].Equals(anchorLine))
            {
                anchorLineIndex = idx;
                break;
            }
        }

        int medianLineHeight = ComputeMedianLineHeight(ocr.Lines, anchorLine);
        // 行宽基准取 max(锚点行宽, 中位行宽)：锚点落在段末短行时，
        // 仍允许吸入同段落的全宽正文行，只拦远超正文宽度的 UI 栏。
        int widthBaseline = Math.Max(anchorLine.Box.Width, ComputeMedianLineWidth(ocr.Lines));
        int maxCandidateWidth = (int)Math.Round(widthBaseline * opts.BlockMaxWidthVsMedianFactor);
        int coreWidthLimit = (int)Math.Round(widthBaseline * opts.BlockCoreWidthFactor);

        // 自适应行间距上限：取“固定比例×行高”与“实测中位行间距×因子”的更严值，
        // 紧凑排版下段落间距小于 0.5×行高时也能停在段落边界。
        double gapLimit = opts.BlockMaxVerticalGapFactor * medianLineHeight;
        if (TryComputeMedianLineGap(ocr.Lines, out int medianLineGap))
        {
            double adaptive = Math.Max(opts.BlockParagraphGapMinPx, opts.BlockParagraphGapVsMedianFactor * medianLineGap);
            gapLimit = Math.Min(gapLimit, adaptive);
        }

        // 段落几何基线：中位右缘/中位行宽用于段末短行判定
        int medianRight = ComputeMedianRight(ocr.Lines);
        int medianLineWidth = ComputeMedianLineWidth(ocr.Lines);

        // 列聚类：多栏布局下把块生长限制在锚点所在列内，防止垂直相邻的另一栏文本被吸入同一段。
        // 容差 = 中位行高 × 因子：同栏各行的左缘抖动（缩进/项目符号）远小于一行高，
        // 超过该值的左缘差异视为不同列。
        double columnTolerance = medianLineHeight * opts.BlockColumnClusterToleranceFactor;
        var columnClusters = ClusterByLeftEdge(ocr.Lines, columnTolerance);
        // 多栏守卫：仅当「簇数 ≥ 2 且 含 ≥2 行的簇 ≥ 2」时认定多栏成立。
        // 原因：居中排版的左缘小步长漂移若被误判为多列会零回归破坏；
        // 单栏（聚成一簇）或碎片化（大量单行簇）都不启用列限制，保持旧版行为。
        bool multiColumn = false;
        int anchorClusterIndex = -1;
        if (columnClusters.Count >= 2)
        {
            int multiRowClusterCount = 0;
            foreach (var c in columnClusters) if (c.Count >= 2) multiRowClusterCount++;
            if (multiRowClusterCount >= 2)
            {
                anchorClusterIndex = FindAnchorClusterIndex(columnClusters, anchorLine);
                if (anchorClusterIndex >= 0)
                    multiColumn = true;
            }
        }

        PhysicalRect union = anchorLine.Box;
        // 核心列并集：只由正文宽度行（≤ coreWidthLimit）累积。水平连通性判定基于它，
        // 防止超宽行把 union 撑宽后将水平不连续的附近文本（另一栏/隔开的文本）桥接进来。
        // 锚点行宽 ≤ widthBaseline ≤ coreWidthLimit，初始必为核心行。
        PhysicalRect coreUnion = anchorLine.Box;
        int coreCount = 1;
        List<OcrLine> selected = new() { anchorLine };

        for (int i = anchorLineIndex - 1; i >= 0; i--)
        {
            if (selected.Count >= opts.BlockMaxLinesPerBlock) break;
            OcrLine candidate = ocr.Lines[i];
            // 列聚类约束（多栏时）：候选不在锚点列 → 停止生长（break 保持段末/缩进等既有语义不变）
            if (multiColumn && !IsInAnchorCluster(candidate, columnClusters[anchorClusterIndex]))
                break;
            // 上方候选是段末短行（且块内已有全宽行）：它是上一段落的结尾 → 停在边界前，不纳入
            if (IsShortTail(candidate, medianRight, medianLineWidth, opts) && HasFullWidthLine(selected, medianRight, medianLineWidth, opts))
                break;
            if (!CheckCandidate(candidate, union, coreUnion, medianLineHeight, maxCandidateWidth, gapLimit, opts)) break;
            if (ExceedsCoreRightGrowth(candidate, coreUnion, coreCount, coreWidthLimit, medianLineHeight, opts)) break;

            // 候选行左缘明显右移（首行缩进）：它是当前段落的首行 → 纳入后停止，不跨入上一段落
            bool indentedFirstLine = candidate.Box.Left - coreUnion.Left > opts.BlockFirstLineIndentFactor * medianLineHeight;
            union = UnionRect(union, candidate.Box);
            if (candidate.Box.Width <= coreWidthLimit)
            {
                coreUnion = UnionRect(coreUnion, candidate.Box);
                coreCount++;
            }
            selected.Insert(0, candidate);
            if (indentedFirstLine) break;
        }

        for (int i = anchorLineIndex + 1; i < ocr.Lines.Count; i++)
        {
            if (selected.Count >= opts.BlockMaxLinesPerBlock) break;
            OcrLine candidate = ocr.Lines[i];
            // 列聚类约束（多栏时）：候选不在锚点列 → 停止生长
            if (multiColumn && !IsInAnchorCluster(candidate, columnClusters[anchorClusterIndex]))
                break;
            // 块当前末行是段末短行（且块内已有全宽行）：段落已结束 → 不再吸入下一段落
            if (IsShortTail(selected[^1], medianRight, medianLineWidth, opts) && HasFullWidthLine(selected, medianRight, medianLineWidth, opts))
                break;
            // 候选行左缘明显右移（下一段落的首行缩进）→ 停在段落边界前
            if (candidate.Box.Left - coreUnion.Left > opts.BlockMaxLeftEdgeDeltaFactor * medianLineHeight)
                break;
            if (!CheckCandidate(candidate, union, coreUnion, medianLineHeight, maxCandidateWidth, gapLimit, opts)) break;
            if (ExceedsCoreRightGrowth(candidate, coreUnion, coreCount, coreWidthLimit, medianLineHeight, opts)) break;
            union = UnionRect(union, candidate.Box);
            if (candidate.Box.Width <= coreWidthLimit)
            {
                coreUnion = UnionRect(coreUnion, candidate.Box);
                coreCount++;
            }
            selected.Add(candidate);
        }

        string blockText = string.Join("\n", selected.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        bool noBlockFound = string.IsNullOrEmpty(blockText);

        return new BlockSelectionResult(
            BlockText: noBlockFound ? null : blockText,
            UnionBox: union,
            SelectedLines: selected.AsReadOnly(),
            Kind: SelectionKind.Block,
            OperationId: Guid.NewGuid(),
            NoBlockFound: noBlockFound);
    }

    private static OcrLine FindAnchorLine(
        IReadOnlyList<OcrLine> lines, PhysicalPoint anchor, out bool contained, out double bestDistance)
    {
        OcrLine? containing = lines.FirstOrDefault(l => l.Box.Contains(anchor));
        if (containing != null)
        {
            contained = true;
            bestDistance = 0;
            return containing;
        }

        contained = false;
        OcrLine best = lines[0];
        bestDistance = double.MaxValue;
        foreach (var line in lines)
        {
            double dist = DistanceToRect(anchor, line.Box);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = line;
            }
        }
        return best;
    }

    private static double DistanceToRect(PhysicalPoint p, PhysicalRect box)
    {
        int dx = 0;
        if (p.X < box.Left) dx = box.Left - p.X;
        else if (p.X >= box.Right) dx = p.X - (box.Right - 1);

        int dy = 0;
        if (p.Y < box.Top) dy = box.Top - p.Y;
        else if (p.Y >= box.Bottom) dy = p.Y - (box.Bottom - 1);

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int ComputeMedianLineHeight(IReadOnlyList<OcrLine> lines, OcrLine anchorLine)
    {
        if (lines.Count < 3) return anchorLine.Box.Height;

        var heights = lines.Select(l => l.Box.Height).OrderBy(h => h).ToList();
        int mid = heights.Count / 2;
        return heights[mid];
    }

    private static int ComputeMedianLineWidth(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return 0;
        var widths = lines.Select(l => l.Box.Width).OrderBy(w => w).ToList();
        return widths[widths.Count / 2];
    }

    /// <summary>
    /// 核心列右缘增长护栏：核心列已建立（≥2 核心行）后，候选行使核心列右缘
    /// 增长超过 行高×因子 → 选区正横向侵入另一文本区域（跨区域连通）→ 停在边界前。
    /// 只限右缘：左缘外伸（悬挂缩进/项目符号换行）不产生跨区域连通，且由 leftDelta 护栏兼顾；
    /// 只在核心列建立后生效：锚点落在段末短行时仍需允许吸入同段落的全宽行（Case20）。
    /// </summary>
    private static bool ExceedsCoreRightGrowth(OcrLine candidate, PhysicalRect coreUnion, int coreCount, int coreWidthLimit, int medianLineHeight, SelectionOptions opts)
    {
        if (coreCount < 2) return false;
        if (candidate.Box.Width > coreWidthLimit) return false; // 非核心行不累积核心列，不触发护栏
        int rightGrowth = candidate.Box.Right - coreUnion.Right;
        return rightGrowth > opts.BlockMaxCoreGrowthFactor * medianLineHeight;
    }

    private static bool CheckCandidate(OcrLine candidate, PhysicalRect union, PhysicalRect coreUnion, int medianHeight, int maxCandidateWidth, double gapLimit, SelectionOptions opts)
    {
        double heightRatio = candidate.Box.Height / (double)medianHeight;
        if (heightRatio < opts.BlockMinHeightRatio || heightRatio > opts.BlockMaxHeightRatio)
            return false;

        // 宽度护栏：远超正文行宽的全宽 UI 栏/标题栏不吸入块
        if (candidate.Box.Width > maxCandidateWidth)
            return false;

        // 候选行必须与核心列并集水平相交：垂直方向重叠但水平分离的行
        // （另一栏/被空白隔开的文本）不能因 verticalGap 为负而被误连。
        // 用核心列而非全块 union：超宽行撑宽 union 后，不相干的行也会在
        // 其水平范围内“相交”，导致选区跨越断开处连通到附近其他文本。
        if (candidate.Box.Right <= coreUnion.Left || candidate.Box.Left >= coreUnion.Right)
            return false;

        int verticalGap;
        if (candidate.Box.Bottom <= union.Top)
            verticalGap = union.Top - candidate.Box.Bottom;
        else
            verticalGap = candidate.Box.Top - union.Bottom;
        if (verticalGap > gapLimit)
            return false;

        double xOverlapRatio = ComputeOverlapRatio(candidate.Box, coreUnion);
        int leftDelta = Math.Abs(candidate.Box.Left - coreUnion.Left);
        if (!(xOverlapRatio >= opts.BlockMinXOverlap || leftDelta <= opts.BlockMaxLeftEdgeDeltaFactor * medianHeight))
            return false;

        return true;
    }

    /// <summary>
    /// 相邻行垂直间距的中位数（按 Top 排序，取低位中位数使护栏更严）。
    /// 只统计水平相交的行对：多栏布局中不同栏的行在 Y 序上相邻但水平分离，
    /// 其间距（常为 0/负）会污染基线导致护栏过严。至少 2 个样本才返回 true。
    /// </summary>
    private static bool TryComputeMedianLineGap(IReadOnlyList<OcrLine> lines, out int medianGap)
    {
        medianGap = 0;
        if (lines.Count < 3) return false;

        var sorted = lines.OrderBy(l => l.Box.Top).ToList();
        var gaps = new List<int>(sorted.Count - 1);
        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1].Box;
            var cur = sorted[i].Box;
            if (cur.Right <= prev.Left || cur.Left >= prev.Right)
                continue; // 水平分离（另一栏）的行对不计入行距基线
            gaps.Add(Math.Max(0, cur.Top - prev.Bottom));
        }

        if (gaps.Count < 2) return false;
        gaps.Sort();
        medianGap = gaps[(gaps.Count - 1) / 2];
        return true;
    }

    private static int ComputeMedianRight(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return 0;
        var rights = lines.Select(l => l.Box.Right).OrderBy(r => r).ToList();
        return rights[(rights.Count - 1) / 2];
    }

    /// <summary>段末短行：右缘距中位右缘超过 中位行宽×因子。正文段末行通常明显短，是段落边界信号。</summary>
    private static bool IsShortTail(OcrLine line, int medianRight, int medianLineWidth, SelectionOptions opts)
    {
        if (medianLineWidth <= 0) return false;
        return medianRight - line.Box.Right > opts.BlockShortTailRightMarginFactor * medianLineWidth;
    }

    /// <summary>块内是否已有全宽正文行：短行护栏仅在此基础上生效，避免误伤全是短行的题注/列表块。</summary>
    private static bool HasFullWidthLine(IReadOnlyList<OcrLine> lines, int medianRight, int medianLineWidth, SelectionOptions opts)
    {
        if (medianLineWidth <= 0) return false;
        return lines.Any(l => medianRight - l.Box.Right <= opts.BlockFullWidthRightMarginFactor * medianLineWidth);
    }

    private static double ComputeOverlapRatio(PhysicalRect a, PhysicalRect b)
    {
        int overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        return overlap / (double)Math.Min(a.Width, b.Width);
    }

    private static PhysicalRect UnionRect(PhysicalRect a, PhysicalRect b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new PhysicalRect(x, y, right - x, bottom - y);
    }

    // 列聚类：按左缘 minX 升序后做链式贪心聚类（相邻排序差 > 容差才开新簇），O(n log n)。
    // 链式比较的意义：居中排版的左缘常呈小步长单调漂移，若与簇基准（最小 minX）比较会随
    // 累积漂移链式碎裂成大量单行簇；只看相邻差即可把缓漂移稳定聚成一列，而真正的跨栏
    // 左缘跳变（远大于一行高）仍会被切断。
    private static List<ColumnCluster> ClusterByLeftEdge(IReadOnlyList<OcrLine> lines, double tolerance)
    {
        if (lines.Count == 0) return new List<ColumnCluster>(0);
        var sorted = lines.OrderBy(l => l.Box.Left).ToList();
        var clusters = new List<ColumnCluster>(sorted.Count);
        var current = new ColumnCluster();
        current.Lines.Add(sorted[0]);
        clusters.Add(current);
        for (int i = 1; i < sorted.Count; i++)
        {
            var line = sorted[i];
            // 链式比较：与前一行（当前簇最后加入者）的左缘差 ≤ 容差则同列
            if (Math.Abs(line.Box.Left - current.Lines[^1].Box.Left) <= tolerance)
            {
                current.Lines.Add(line);
            }
            else
            {
                current = new ColumnCluster();
                current.Lines.Add(line);
                clusters.Add(current);
            }
        }
        return clusters;
    }

    // 锚点簇定位：按成员引用归属（与 IsInAnchorCluster 一致），避免数值重判在簇内
    // 漂移超出容差时把同簇成员误判为异簇。
    private static int FindAnchorClusterIndex(List<ColumnCluster> clusters, OcrLine anchorLine)
    {
        for (int i = 0; i < clusters.Count; i++)
        {
            if (clusters[i].Lines.Contains(anchorLine)) return i;
        }
        return -1;
    }

    private static bool IsInAnchorCluster(OcrLine candidate, ColumnCluster anchorCluster)
    {
        return anchorCluster.Lines.Contains(candidate);
    }

    // 列簇：Lines 为簇内行集合（用于多栏守卫计数、锚点簇定位与成员归属判定）
    private sealed class ColumnCluster
    {
        public List<OcrLine> Lines { get; } = new();
        public int Count => Lines.Count;
    }
}
