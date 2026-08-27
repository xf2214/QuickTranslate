using System.Text;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Selection;

/// <summary>
/// 选中文本探测的采用策略与结果映射（纯函数，无 IO）。
/// 阈值集中于此，对齐 SnapTra 的 120ms 软预算思路；Windows UIA 冷启动更贵，预算放宽到 150ms。
/// </summary>
public static class SelectedTextProbePolicy
{
    /// <summary>探测总预算（毫秒）：超时视为未命中回退 OCR。</summary>
    public const int ProbeBudgetMs = 150;

    /// <summary>单词模式可接受的选中文本长度上限：超过说明用户框选的是段落而非词，交还 OCR。</summary>
    public const int WordModeMaxChars = 240;

    /// <summary>块模式可接受的选中文本长度上限（防误触全文档选择拖垮翻译请求）。</summary>
    public const int BlockModeMaxChars = 6000;

    /// <summary>UIA 未给出几何时用于锚定弹窗的兜底矩形尺寸（宽×高）。</summary>
    public const int FallbackBoxWidth = 120;
    public const int FallbackBoxHeight = 40;

    /// <summary>词模式的逐行命中容差（像素）：光标落在某行矩形外扩该值内视为"指向此选区"。</summary>
    public const int SpatialPadWordPx = 6;

    /// <summary>块模式的逐行命中容差（像素）：块选区常整体包裹句子，行级容差略宽。</summary>
    public const int SpatialPadBlockLinePx = 12;

    /// <summary>块模式并集框的外扩容差（像素）：光标贴着选区边缘按热键时仍算指向该选区。</summary>
    public const int SpatialUnionPadPx = 32;

    /// <summary>
    /// 空间相关性校验：选区必须真的在光标附近才可被采纳。
    /// 防陈旧选区劫持——文档里残留的旧选区（浏览器/编辑器会一直保留）远离当前光标时，
    /// 若照单全收会把无关旧文本顶替用户正指向的内容，造成"识别效果不如原来"。
    /// 词模式：任一行矩形外扩 SpatialPadWordPx 含光标；块模式：任一行含光标，
    /// 或并集框外扩 SpatialUnionPadPx 含光标（选区可整体在光标旁）。
    /// </summary>
    public static bool IsSpatiallyRelevant(
        IReadOnlyList<PhysicalRect> lineRects,
        PhysicalRect unionBox,
        PhysicalPoint cursor,
        bool wordMode)
    {
        int pad = wordMode ? SpatialPadWordPx : SpatialPadBlockLinePx;
        for (int i = 0; i < lineRects.Count; i++)
        {
            var r = lineRects[i];
            if (cursor.X >= r.X - pad && cursor.X <= r.Right + pad &&
                cursor.Y >= r.Y - pad && cursor.Y <= r.Bottom + pad)
            {
                return true;
            }
        }

        if (wordMode)
        {
            return false;
        }

        return cursor.X >= unionBox.X - SpatialUnionPadPx && cursor.X <= unionBox.Right + SpatialUnionPadPx &&
               cursor.Y >= unionBox.Y - SpatialUnionPadPx && cursor.Y <= unionBox.Bottom + SpatialUnionPadPx;
    }

    /// <summary>文本经清洗后是否可被模式采用：非空白且不超过该模式的字符上限。</summary>
    public static bool IsAdoptable(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Length <= maxChars;
    }

    /// <summary>
    /// 展示用文本清洗：统一 CRLF→LF、去首尾空白、折叠 3 连以上换行为 2 连。
    /// 不做其他改动——该文本将直接作为翻译源。
    /// </summary>
    public static string Normalize(string raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        // 1. 统一换行：CRLF→LF，再处理孤立 CR
        // 为保持单趟 StringBuilder 思路，先做替换再进入折叠阶段
        string unified = raw.Replace("\r\n", "\n").Replace("\r", "\n");

        // 2. 去首尾空白（包含 \n、空格、\t 等）
        string trimmed = unified.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // 3. 折叠 3 连以上换行为 2 连 —— 单趟 StringBuilder
        var sb = new StringBuilder(trimmed.Length);
        int consecutiveNewlines = 0;
        foreach (char c in trimmed)
        {
            if (c == '\n')
            {
                consecutiveNewlines++;
                if (consecutiveNewlines <= 2)
                {
                    sb.Append('\n');
                }
                // 超过 2 连则丢弃
            }
            else
            {
                consecutiveNewlines = 0;
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 行矩形并集；空集合时返回以锚点为中心的兜底矩形
    /// （cursor.X-FallbackBoxWidth/2, cursor.Y-FallbackBoxHeight/2, W, H）。
    /// </summary>
    public static PhysicalRect UnionOrFallback(IReadOnlyList<PhysicalRect> rects, PhysicalPoint cursor)
    {
        if (rects.Count == 0)
        {
            return new PhysicalRect(
                cursor.X - FallbackBoxWidth / 2,
                cursor.Y - FallbackBoxHeight / 2,
                FallbackBoxWidth,
                FallbackBoxHeight);
        }

        int minX = rects[0].Left;
        int minY = rects[0].Top;
        int maxR = rects[0].Right;
        int maxB = rects[0].Bottom;

        for (int i = 1; i < rects.Count; i++)
        {
            var r = rects[i];
            if (r.Left < minX) minX = r.Left;
            if (r.Top < minY) minY = r.Top;
            if (r.Right > maxR) maxR = r.Right;
            if (r.Bottom > maxB) maxB = r.Bottom;
        }

        return new PhysicalRect(minX, minY, maxR - minX, maxB - minY);
    }
}
