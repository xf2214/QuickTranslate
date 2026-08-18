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

        PhysicalRect union = anchorLine.Box;
        List<OcrLine> selected = new() { anchorLine };

        for (int i = anchorLineIndex - 1; i >= 0; i--)
        {
            if (selected.Count >= opts.BlockMaxLinesPerBlock) break;
            OcrLine candidate = ocr.Lines[i];
            if (!CheckCandidate(candidate, union, medianLineHeight, maxCandidateWidth, opts)) break;
            union = UnionRect(union, candidate.Box);
            selected.Insert(0, candidate);
        }

        for (int i = anchorLineIndex + 1; i < ocr.Lines.Count; i++)
        {
            if (selected.Count >= opts.BlockMaxLinesPerBlock) break;
            OcrLine candidate = ocr.Lines[i];
            if (!CheckCandidate(candidate, union, medianLineHeight, maxCandidateWidth, opts)) break;
            union = UnionRect(union, candidate.Box);
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

    private static bool CheckCandidate(OcrLine candidate, PhysicalRect union, int medianHeight, int maxCandidateWidth, SelectionOptions opts)
    {
        double heightRatio = candidate.Box.Height / (double)medianHeight;
        if (heightRatio < opts.BlockMinHeightRatio || heightRatio > opts.BlockMaxHeightRatio)
            return false;

        // 宽度护栏：远超正文行宽的全宽 UI 栏/标题栏不吸入块
        if (candidate.Box.Width > maxCandidateWidth)
            return false;

        // 候选行必须与块并集水平相交：垂直方向重叠但水平完全分离的行
        // （如同一高度的另一栏文字）不能因 verticalGap 为负而被误连。
        if (candidate.Box.Right <= union.Left || candidate.Box.Left >= union.Right)
            return false;

        int verticalGap;
        if (candidate.Box.Bottom <= union.Top)
            verticalGap = union.Top - candidate.Box.Bottom;
        else
            verticalGap = candidate.Box.Top - union.Bottom;
        if (verticalGap > opts.BlockMaxVerticalGapFactor * medianHeight)
            return false;

        double xOverlapRatio = ComputeOverlapRatio(candidate.Box, union);
        int leftDelta = Math.Abs(candidate.Box.Left - union.Left);
        if (!(xOverlapRatio >= opts.BlockMinXOverlap || leftDelta <= opts.BlockMaxLeftEdgeDeltaFactor * medianHeight))
            return false;

        return true;
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
}
