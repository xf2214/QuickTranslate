using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Selection;

public record BlockSelectionResult(
    string? BlockText,
    PhysicalRect UnionBox,
    IReadOnlyList<OcrLine> SelectedLines,
    SelectionKind Kind = SelectionKind.Block,
    Guid OperationId = default,
    bool NoBlockFound = false);
