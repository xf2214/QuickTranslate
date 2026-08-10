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

        OcrLine anchorLine = FindAnchorLine(ocr.Lines, anchor);
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

        PhysicalRect union = anchorLine.Box;
        List<OcrLine> selected = new() { anchorLine };

        for (int i = anchorLineIndex - 1; i >= 0; i--)
        {
            if (selected.Count >= opts.BlockMaxLinesPerBlock) break;
            OcrLine candidate = ocr.Lines[i];
            if (!CheckCandidate(candidate, union, medianLineHeight, opts)) break;
            union = UnionRect(union, candidate.Box);
            selected.Insert(0, candidate);
        }

        for (int i = anchorLineIndex + 1; i < ocr.Lines.Count; i++)
        {
            if (selected.Count >= opts.BlockMaxLinesPerBlock) break;
            OcrLine candidate = ocr.Lines[i];
            if (!CheckCandidate(candidate, union, medianLineHeight, opts)) break;
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

    private static OcrLine FindAnchorLine(IReadOnlyList<OcrLine> lines, PhysicalPoint anchor)
    {
        OcrLine? containing = lines.FirstOrDefault(l => l.Box.Contains(anchor));
        if (containing != null) return containing;

        OcrLine best = lines[0];
        double bestDist = double.MaxValue;
        foreach (var line in lines)
        {
            int cx = line.Box.X + line.Box.Width / 2;
            int cy = line.Box.Y + line.Box.Height / 2;
            double dist = Math.Sqrt(Math.Pow(cx - anchor.X, 2) + Math.Pow(cy - anchor.Y, 2));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = line;
            }
        }
        return best;
    }

    private static int ComputeMedianLineHeight(IReadOnlyList<OcrLine> lines, OcrLine anchorLine)
    {
        if (lines.Count < 3) return anchorLine.Box.Height;

        var heights = lines.Select(l => l.Box.Height).OrderBy(h => h).ToList();
        int mid = heights.Count / 2;
        return heights[mid];
    }

    private static bool CheckCandidate(OcrLine candidate, PhysicalRect union, int medianHeight, SelectionOptions opts)
    {
        double heightRatio = candidate.Box.Height / (double)medianHeight;
        if (heightRatio < opts.BlockMinHeightRatio || heightRatio > opts.BlockMaxHeightRatio)
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
