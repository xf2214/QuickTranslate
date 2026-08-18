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
                // 几何合理性：单字符宽超过行高上限 → 比例法兜底把短文本摊到整行宽的
                // 异常框（如 Text='y' Box=650x60），拒绝以免画出超大选框。
                // 用整行高度而非词框高做基准：词框经过垂直收紧（如小字号拼音/下标），
                // 用词框高会把正常词误拒。
                int refHeight = Math.Max(line.Box.Height, c.Box.Height);
                if (c.Box.Width > c.Text.Length * refHeight * opts.MaxWordWidthPerCharHeightFactor)
                    continue;

                candidatesTaken++;

                // 包含判定用宽容模式：词框经过垂直收紧（只贴墨水范围），光标常落在
                // 词框垂直范围外导致 Contains 失败，退而选“附近最近的词”造成选框偏移。
                // 同一行内水平命中即视为指向该词（行的垂直范围由行框覆盖）。
                if (ContainsTolerant(c.Box, line.Box, anchor))
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

    /// <summary>
    /// 宽容包含判定：X 落在词框水平范围内，且 Y 落在所属行框的垂直范围内
    /// （上下各留少量容差）。词框垂直收紧后高度只贴墨水，直接用 Contains
    /// 会把光标在字形上下边缘附近的正常指向误判为未命中。
    /// </summary>
    private static bool ContainsTolerant(PhysicalRect wordBox, PhysicalRect lineBox, PhysicalPoint anchor)
    {
        if (anchor.X < wordBox.Left || anchor.X >= wordBox.Right) return false;
        int pad = Math.Max(2, lineBox.Height / 6);
        return anchor.Y >= lineBox.Top - pad && anchor.Y < lineBox.Bottom + pad;
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
