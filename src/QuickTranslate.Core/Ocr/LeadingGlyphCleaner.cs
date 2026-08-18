using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

/// <summary>
/// 行首单符号清理：UI 图标（复选框/单选钮/列表符等）常被 OCR 识别成孤立单字符
/// （如 ?、0、•），混入翻译文本并撑大行框。
/// 判定：首词为单字符，且与第二个词之间存在明显空隙（正常文本行首孤立单符号后
/// 不会有空隙，如 #include 的 # 紧贴正文）→ 移除该词并把行框收紧到剩余词。
/// </summary>
public static class LeadingGlyphCleaner
{
    public static List<OcrLine> Clean(IReadOnlyList<OcrLine> lines, out int cleanedCount)
    {
        cleanedCount = 0;
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var cleaned = CleanLine(line);
            if (!ReferenceEquals(cleaned, line)) cleanedCount++;
            result.Add(cleaned);
        }
        return result;
    }

    private static OcrLine CleanLine(OcrLine line)
    {
        var words = line.Words;
        if (words.Count < 2) return line;

        var first = words[0];
        if (first.Text.Length != 1) return line;

        char c = first.Text[0];
        int gap = words[1].Box.Left - first.Box.Right;
        int minGap = Math.Max(2, line.Box.Height / 8);
        if (gap < minGap) return line;

        // 符号类（?/•/· 等非字母数字非汉字）直接命中；
        // 数字类（图标 ○ 误识别为 0）要求更大空隙，避免误伤 "1 + 2" 等代码行。
        bool isSymbol = !char.IsLetterOrDigit(c) && !IsCjk(c);
        bool isMisreadDigit = char.IsDigit(c) && gap >= line.Box.Height / 4;
        if (!isSymbol && !isMisreadDigit) return line;

        var rest = words.Skip(1).ToList();
        int left = rest.Min(w => w.Box.Left);
        int top = rest.Min(w => w.Box.Top);
        int right = rest.Max(w => w.Box.Right);
        int bottom = rest.Max(w => w.Box.Bottom);
        var box = new PhysicalRect(left, top, right - left, bottom - top);
        return new OcrLine(box, rest, string.Join(" ", rest.Select(w => w.Text)), line.AngleDeg);
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;
}
