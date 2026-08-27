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
        var h2 = new List<(WordCandidate Candidate, string LineText, PhysicalRect LineBox, double Distance)>();

        for (int i = 0; i < ocr.Lines.Count; i++)
        {
            var line = ocr.Lines[i];
            var candidates = _resolver.Resolve(line, i);
            var effectiveMax = opts.ComputeEffectiveMax(line.Box.Height);

            // 词框最小高度 = max(绝对下限, 行高 × 比例下限)：行内细条碎片词
            // （det 渗漏/识别碎片产物）高度占行高比例异常低，直接拒绝。
            int minWordHeight = Math.Max(
                opts.MinWordHeight,
                (int)(line.Box.Height * opts.MinWordHeightLineRatio));

            int candidatesTaken = 0;
            foreach (var c in candidates)
            {
                if (candidatesTaken >= opts.MaxCandidatesPerLine)
                    break;

                if (c.Box.Width < opts.MinWordWidth || c.Box.Height < minWordHeight)
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
                        h2.Add((c, line.Text, line.Box, distance));
                    }
                }
            }
        }

        if (h1.Count > 0)
        {
            // 排序分两级：
            // 1) 光标真正落在词框内（含少量 padding）的候选优先，按面积取小
            //    （嵌套框场景选更精确的内框），平局取中心更近者；
            // 2) 仅靠行容差命中的候选（光标在两行容差重叠区等）不再按面积取小——
            //    那样会选中另一行的小词导致选框偏离鼠标，改按到词框距离取近，
            //    平局取中心更近者。
            var insideHits = h1.Where(x => BoxContainsPadded(x.Candidate.Box, anchor)).ToList();
            var best = insideHits.Count > 0
                ? insideHits
                    .OrderBy(x => x.Candidate.Box.Width * x.Candidate.Box.Height)
                    .ThenBy(x => ComputeDistanceToCenter(anchor, x.Candidate.Box))
                    .First()
                : h1
                    .OrderBy(x => ComputeDistance(anchor, x.Candidate.Box))
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
            // 排序分四级（日志实测：光标所在行识别失败/无有效候选时，旧纯欧氏距离
            // 会选中相邻行上水平更近的词——如光标 Y=780 选到 Y=804 下一行的字）：
            // 1) 候选所属行框到光标的垂直间距小者优先：光标指向的行永远优先于邻行；
            // 2) 同行内按到词框的水平间距取近；
            // 3) 平局回退欧氏距离、再到词框中心距离。
            var best = h2
                .OrderBy(x => VerticalGap(anchor, x.LineBox))
                .ThenBy(x => HorizontalGap(anchor, x.Candidate.Box))
                .ThenBy(x => x.Distance)
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

        return new SelectionResult(
            Text: null,
            ContextLine: null,
            Box: default,
            Kind: SelectionKind.Word,
            Confidence: null,
            OperationId: Guid.NewGuid(),
            NoTextFound: true);
    }

    /// <summary>光标是否落在词框内（四周各留 2px 容差），用于区分“真命中”与“行容差命中”。</summary>
    private static bool BoxContainsPadded(PhysicalRect box, PhysicalPoint anchor)
    {
        const int pad = 2;
        return anchor.X >= box.Left - pad && anchor.X < box.Right + pad &&
               anchor.Y >= box.Top - pad && anchor.Y < box.Bottom + pad;
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

    /// <summary>点到框的垂直间距：框外为到最近边缘的距离，框内为 0。</summary>
    private static double VerticalGap(PhysicalPoint p, PhysicalRect box)
    {
        if (p.Y < box.Top) return box.Top - p.Y;
        if (p.Y >= box.Bottom) return p.Y - (box.Bottom - 1);
        return 0;
    }

    /// <summary>点到框的水平间距：框外为到最近边缘的距离，框内为 0。</summary>
    private static double HorizontalGap(PhysicalPoint p, PhysicalRect box)
    {
        if (p.X < box.Left) return box.Left - p.X;
        if (p.X >= box.Right) return p.X - (box.Right - 1);
        return 0;
    }
}
