using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Selection;

public interface IWordSelector
{
    SelectionResult SelectWord(OcrLayoutResult ocr, PhysicalPoint anchor, SelectionOptions? opts = null);
}
