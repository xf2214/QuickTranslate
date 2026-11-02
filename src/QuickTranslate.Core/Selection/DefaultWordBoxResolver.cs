using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Selection;

public class DefaultWordBoxResolver : IWordBoxResolver
{
    public IReadOnlyList<WordCandidate> Resolve(OcrLine line, int lineIndex)
    {
        if (line.Words.Count > 0)
        {
            var result = new List<WordCandidate>(line.Words.Count);
            for (int i = 0; i < line.Words.Count; i++)
            {
                var w = line.Words[i];
                AddCandidatesForWord(result, lineIndex, i, w.Box, w.Text, w.Confidence);
            }
            return result;
        }

        var tokens = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return Array.Empty<WordCandidate>();
        }

        int totalChars = tokens.Sum(t => t.Length);
        if (totalChars == 0)
        {
            return Array.Empty<WordCandidate>();
        }

        var candidates = new List<WordCandidate>(tokens.Length);
        int currentX = line.Box.X;
        int lineY = line.Box.Y;
        int lineHeight = line.Box.Height;
        int lineWidth = line.Box.Width;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            int tokenWidth = (int)Math.Round((double)token.Length / totalChars * lineWidth);

            if (i == tokens.Length - 1)
            {
                tokenWidth = line.Box.Right - currentX;
            }

            var rawBox = new PhysicalRect(currentX, lineY, tokenWidth, lineHeight);
            currentX += tokenWidth;

            AddCandidatesForWord(candidates, lineIndex, i, rawBox, token, 1.0f);
        }

        return candidates;
    }

    private static void AddCandidatesForWord(
        List<WordCandidate> dest,
        int lineIndex,
        int wordIndex,
        PhysicalRect rawBox,
        string rawText,
        float confidence)
    {
        if (string.IsNullOrEmpty(rawText)) return;

        // 1. 标点清洗与物理框收敛：去除外围标点并按比例收缩物理框，避免标点符号被圈入词框
        var (cleanStart, cleanLength) = TextRunSplitter.TrimPeripheralPunctuation(rawText);
        if (cleanLength == 0) return;

        string cleanText = (cleanStart == 0 && cleanLength == rawText.Length)
            ? rawText
            : rawText.Substring(cleanStart, cleanLength);

        PhysicalRect cleanBox = ComputeSubBox(rawBox, rawText, cleanStart, cleanLength);
        dest.Add(new WordCandidate(lineIndex, wordIndex, cleanBox, cleanText, confidence));
    }

    public static PhysicalRect ComputeSubBox(PhysicalRect parentBox, string fullText, int subStart, int subLength)
    {
        if (subStart == 0 && subLength == fullText.Length)
            return parentBox;

        double totalWeight = 0.0;
        for (int i = 0; i < fullText.Length; i++)
            totalWeight += TextRunSplitter.CharWeight(fullText[i]);
        if (totalWeight <= 0) totalWeight = fullText.Length;

        double leadWeight = 0.0;
        for (int i = 0; i < subStart; i++)
            leadWeight += TextRunSplitter.CharWeight(fullText[i]);

        double subWeight = 0.0;
        for (int i = subStart; i < subStart + subLength; i++)
            subWeight += TextRunSplitter.CharWeight(fullText[i]);

        int subX = parentBox.X + (int)Math.Round(parentBox.Width * (leadWeight / totalWeight));
        int subW = (int)Math.Round(parentBox.Width * (subWeight / totalWeight));
        subW = Math.Max(1, Math.Min(subW, parentBox.Right - subX));
        return new PhysicalRect(subX, parentBox.Y, subW, parentBox.Height);
    }
}
