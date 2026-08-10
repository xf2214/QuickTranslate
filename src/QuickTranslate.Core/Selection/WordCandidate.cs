using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Selection;

public record WordCandidate(int LineIndex, int WordIndex, PhysicalRect Box, string Text, float Confidence);
