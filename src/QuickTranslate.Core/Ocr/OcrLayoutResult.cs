using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

public record OcrLayoutResult(
    PhysicalRect CaptureRegion,
    IReadOnlyList<OcrLine> Lines,
    OcrTimings Timings,
    DateTimeOffset CaptureTime,
    uint? DpiX = null,
    uint? DpiY = null,
    string? EngineName = null,
    bool FromCache = false)
{
    public int LineCount => Lines.Count;
}
