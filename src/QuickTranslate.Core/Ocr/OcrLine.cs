using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

public record OcrLine
{
    public PhysicalRect Box { get; }
    public IReadOnlyList<OcrWord> Words { get; }
    public string Text { get; }
    public float? AngleDeg { get; }

    /// <summary>
    /// 行级识别置信度（CTC 解码输出字符的平均概率，0-1）。
    /// 引擎未提供时（如 MockOcr/测试构造）为 null。
    /// </summary>
    public float? Confidence { get; }

    public OcrLine(PhysicalRect box, IReadOnlyList<OcrWord> words, string? text = null, float? angleDeg = null, float? confidence = null)
    {
        Box = box;
        Words = words ?? throw new ArgumentNullException(nameof(words));
        Text = text ?? string.Join(" ", words.Select(w => w.Text));
        AngleDeg = angleDeg;
        Confidence = confidence;
    }
}
