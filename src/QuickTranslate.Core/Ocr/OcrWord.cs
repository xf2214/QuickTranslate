using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

public record OcrWord(PhysicalRect Box, string Text, float Confidence, int LineIndex);
