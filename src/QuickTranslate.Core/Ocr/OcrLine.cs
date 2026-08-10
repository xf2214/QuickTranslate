using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

public record OcrLine
{
    public PhysicalRect Box { get; }
    public IReadOnlyList<OcrWord> Words { get; }
    public string Text { get; }
    public float? AngleDeg { get; }

    public OcrLine(PhysicalRect box, IReadOnlyList<OcrWord> words, string? text = null, float? angleDeg = null)
    {
        Box = box;
        Words = words ?? throw new ArgumentNullException(nameof(words));
        Text = text ?? string.Join(" ", words.Select(w => w.Text));
        AngleDeg = angleDeg;
    }
}
