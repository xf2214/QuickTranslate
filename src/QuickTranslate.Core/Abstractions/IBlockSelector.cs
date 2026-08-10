using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Selection;

namespace QuickTranslate.Core.Abstractions;

public interface IBlockSelector
{
    BlockSelectionResult SelectBlock(OcrLayoutResult ocr, PhysicalPoint anchor, SelectionOptions? opts = null);
}
