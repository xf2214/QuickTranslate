using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Infrastructure.Ocr;

internal static class OcrRegionSelector
{
    internal static List<PhysicalRect> FilterByFocusBand(
        List<PhysicalRect> boxes, PhysicalRect frameRegion, PhysicalRect? focusBand)
    {
        if (!focusBand.HasValue) return boxes;
        var band = focusBand.Value;
        var selected = boxes.Where(b => b.Y + frameRegion.Y < band.Bottom
            && b.Bottom + frameRegion.Y > band.Top).ToList();
        // The caller already bounds the region. A fixed line cap silently
        // truncates paragraphs and prevents a wider retry from filling them in.
        return selected;
    }
}
