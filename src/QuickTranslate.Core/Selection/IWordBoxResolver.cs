using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Selection;

public interface IWordBoxResolver
{
    IReadOnlyList<WordCandidate> Resolve(OcrLine line, int lineIndex);
}
