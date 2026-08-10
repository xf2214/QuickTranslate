using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Selection;

public class WordSelector : IWordSelector
{
    private readonly IWordBoxResolver _resolver;

    public WordSelector(IWordBoxResolver resolver)
    {
        _resolver = resolver;
    }

    public SelectionResult SelectWord(OcrLayoutResult ocr, PhysicalPoint anchor, SelectionOptions? opts = null)
    {
        opts = opts ?? SelectionOptions.Default;

        var h1 = new List<(WordCandidate Candidate, string LineText)>();
        var h2 = new List<(WordCandidate Candidate, string LineText, double Distance)>();

        for (int i = 0; i < ocr.Lines.Count; i++)
        {
            var line = ocr.Lines[i];
            var candidates = _resolver.Resolve(line, i);
            var effectiveMax = opts.ComputeEffectiveMax(line.Box.Height);

            int candidatesTaken = 0;
            foreach (var c in candidates)
            {
                if (candidatesTaken >= opts.MaxCandidatesPerLine)
                    break;

                if (c.Box.Width < opts.MinWordWidth || c.Box.Height < opts.MinWordHeight)
                    continue;
                if (c.Confidence < opts.ConfidenceFloor)
                    continue;

                candidatesTaken++;

                if (c.Box.Contains(anchor))
                {
                    h1.Add((c, line.Text));
                }
                else
                {
                    double distance = ComputeDistance(anchor, c.Box);
                    if (distance < effectiveMax)
                    {
                        h2.Add((c, line.Text, distance));
                    }
                }
            }
        }

        if (h1.Count > 0)
        {
            var best = h1
                .OrderBy(x => x.Candidate.Box.Width * x.Candidate.Box.Height)
                .ThenBy(x => ComputeDistanceToCenter(anchor, x.Candidate.Box))
                .First();

            return new SelectionResult(
                Text: best.Candidate.Text,
                ContextLine: best.LineText,
                Box: best.Candidate.Box,
                Kind: SelectionKind.Word,
                Confidence: best.Candidate.Confidence,
                OperationId: Guid.NewGuid());
        }

        if (h2.Count > 0)
        {
            var best = h2
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Candidate.Box.Width * x.Candidate.Box.Height)
                .First();

            return new SelectionResult(
                Text: best.Candidate.Text,
                ContextLine: best.LineText,
                Box: best.Candidate.Box,
                Kind: SelectionKind.Word,
                Confidence: best.Candidate.Confidence,
                OperationId: Guid.NewGuid());
        }

        return new SelectionResult(
            Text: null,
            ContextLine: null,
            Box: default,
            Kind: SelectionKind.Word,
            Confidence: null,
            OperationId: Guid.NewGuid(),
            NoTextFound: true);
    }

    private static double ComputeDistance(PhysicalPoint anchor, PhysicalRect box)
    {
        int dx = 0;
        if (anchor.X < box.Left) dx = box.Left - anchor.X;
        else if (anchor.X >= box.Right) dx = anchor.X - (box.Right - 1);

        int dy = 0;
        if (anchor.Y < box.Top) dy = box.Top - anchor.Y;
        else if (anchor.Y >= box.Bottom) dy = anchor.Y - (box.Bottom - 1);

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double ComputeDistanceToCenter(PhysicalPoint anchor, PhysicalRect box)
    {
        double cx = box.X + box.Width / 2.0;
        double cy = box.Y + box.Height / 2.0;
        double dx = anchor.X - cx;
        double dy = anchor.Y - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
