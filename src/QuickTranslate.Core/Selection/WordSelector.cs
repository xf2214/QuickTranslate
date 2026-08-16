using System.Text.RegularExpressions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Selection;

public class WordSelector : IWordSelector
{
    private readonly IWordBoxResolver _resolver;
    // Alt+1 取词：候选必须是"词"——包含英文字母（英文单词，允许撇号/连字符），
    // 或包含 CJK 字符（中文单字/词组）。过滤纯标点、纯数字、纯空白。
    private static readonly Regex WordLike = new(
        @"[A-Za-z][A-Za-z'\-]*[A-Za-z]|[A-Za-z]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsWordLike(string text)
    {
        if (WordLike.IsMatch(text)) return true;
        // CJK：中日韩统一表意文字 + 扩展A + 假名 + 谚文
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF ||
                c >= 0x3040 && c <= 0x30FF || c >= 0xAC00 && c <= 0xD7AF)
                return true;
        }
        return false;
    }

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
                // Word mode (Alt+1) 只考虑像"词"的候选：英文单词或 CJK 文字，
                // 过滤纯标点/数字/空白。
                if (string.IsNullOrWhiteSpace(c.Text)) continue;
                if (!IsWordLike(c.Text)) continue;

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
