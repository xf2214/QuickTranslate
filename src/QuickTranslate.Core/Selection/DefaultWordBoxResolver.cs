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
                result.Add(new WordCandidate(lineIndex, i, w.Box, w.Text, w.Confidence));
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

            var box = new PhysicalRect(currentX, lineY, tokenWidth, lineHeight);
            candidates.Add(new WordCandidate(lineIndex, i, box, token, 1.0f));
            currentX += tokenWidth;
        }

        return candidates;
    }
}
