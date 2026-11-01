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

        // The 2px-wide focus marker is used only by Block mode: unlike
        // Word mode (which passes a full-width focus strip), it also encodes
        // the horizontal cursor coordinate. Detect the anchor's text column
        // before running expensive ONNX recognition on the neighboring column.
        // Keep the legacy vertical-only behavior if no nearby anchor exists:
        // the pointer may be between rows, in an image, or outside the text.
        if (band.Width > 4 || selected.Count <= 1)
            return selected;

        int anchorX = band.Left;
        int anchorY = band.Top + band.Height / 2;
        PhysicalRect? anchorLine = null;
        int bestDistance = int.MaxValue;
        foreach (var box in selected)
        {
            int left = box.Left + frameRegion.Left;
            int right = box.Right + frameRegion.Left;
            if (anchorX < left || anchorX >= right)
                continue;

            int top = box.Top + frameRegion.Top;
            int bottom = box.Bottom + frameRegion.Top;
            int distance = anchorY < top ? top - anchorY
                         : anchorY >= bottom ? anchorY - bottom + 1 : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                anchorLine = box;
            }
        }

        if (!anchorLine.HasValue ||
            bestDistance > Math.Max(48, anchorLine.Value.Height * 2))
            return selected;

        var column = anchorLine.Value;
        // Intersect the actual anchor line's horizontal span, rather than
        // requiring every row to include the cursor X: indented/short tail
        // rows in a paragraph can be narrower than the pointer coordinate.
        var sameColumn = selected.Where(box =>
            Math.Min(box.Right, column.Right) > Math.Max(box.Left, column.Left)).ToList();
        return sameColumn.Count > 0 ? sameColumn : selected;
    }
}
