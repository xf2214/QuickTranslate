using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Selection;

public record SelectionResult(
    string? Text,
    string? ContextLine,
    PhysicalRect Box,
    SelectionKind Kind,
    float? Confidence,
    Guid OperationId,
    bool NoTextFound = false);
