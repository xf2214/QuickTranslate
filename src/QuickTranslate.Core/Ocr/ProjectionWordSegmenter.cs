using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

/// <summary>
/// 垂直投影词切分（spec 8.3：空白间隔/垂直投影优先于字符区间估计）。
/// 对 OCR 行裁剪位图做列墨水投影，按空白间隔切出每个词的真实像素边界，
/// 生成屏幕绝对坐标的词框。任一校验不过即返回 false，由调用方回退比例法。
/// </summary>
public static class ProjectionWordSegmenter
{
    // 词间空白列的最小宽度：max(2, 行高 × 0.16)。词间空格 ≈ 0.25 × 字号 ≈ 0.2 × 行框高，
    // 取 0.16 留安全余量，避免字距较大的字体被误切。
    private const float GapHeightFactor = 0.16f;
    private const int GapMinPx = 2;

    // 列墨水数低于该阈值视为空列（抑制孤立噪点）：max(1, 行高 × 0.02)
    private const float InkNoiseFactor = 0.02f;

    // 段最小宽度（像素）
    private const int MinSegmentWidth = 2;

    public static bool TrySegment(
        Bitmap lineBitmap,
        string recognizedText,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        bool rotated180,
        int lineIndex,
        out IReadOnlyList<OcrWord> words)
    {
        words = Array.Empty<OcrWord>();

        if (lineBitmap is null || lineBitmap.Width <= 0 || lineBitmap.Height <= 0)
            return false;
        if (string.IsNullOrWhiteSpace(recognizedText))
            return false;

        var tokens = recognizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        var segments = ExtractInkSegments(lineBitmap);
        if (segments.Count == 0)
            return false;

        // 对齐校验（参考 isValidTokenBoundingBox 思路）：
        // 仅当投影段数与 token 数一致时按从左到右一一映射，否则交由调用方回退。
        if (segments.Count != tokens.Length)
            return false;

        int lineY = frameRegion.Y + localBox.Y;
        int lineHeight = localBox.Height;
        int bmpW = lineBitmap.Width;

        var result = new List<OcrWord>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            var (startX, endX) = segments[i];
            var token = tokens[i];

            if (IsPureCjk(token) && token.Length > 1)
            {
                // 纯 CJK token（无空格分隔的连续中文）：CJK 字形等宽，
                // 把段按字符数均分，逐字生成词框，让框精准贴合光标所指的单字。
                int charCount = token.Length;
                int span = endX - startX;
                for (int ci = 0; ci < charCount; ci++)
                {
                    int cx1 = startX + (int)Math.Round((double)span * ci / charCount);
                    int cx2 = startX + (int)Math.Round((double)span * (ci + 1) / charCount);
                    if (cx2 <= cx1) cx2 = cx1 + 1;
                    result.Add(BuildWord(cx1, cx2, token[ci].ToString(), rotated180, bmpW,
                        localBox, frameRegion, lineY, lineHeight, lineIndex));
                }
            }
            else
            {
                result.Add(BuildWord(startX, endX, token, rotated180, bmpW,
                    localBox, frameRegion, lineY, lineHeight, lineIndex));
            }
        }

        words = result;
        return true;
    }

    private static OcrWord BuildWord(
        int startX, int endX, string text, bool rotated180, int bmpW,
        PhysicalRect localBox, PhysicalRect frameRegion, int lineY, int lineHeight, int lineIndex)
    {
        // 旋转 180° 时把矫正图的 X 翻回原 det 框坐标
        if (rotated180)
        {
            int t = startX;
            startX = bmpW - endX;
            endX = bmpW - t;
        }

        int screenX1 = frameRegion.X + localBox.X + startX;
        int screenX2 = frameRegion.X + localBox.X + endX;
        int w = Math.Max(1, screenX2 - screenX1);
        var box = new PhysicalRect(screenX1, lineY, w, lineHeight);
        return new OcrWord(box, text, 0.9f, lineIndex);
    }

    private static bool IsPureCjk(string token)
    {
        foreach (var c in token)
        {
            bool cjk = c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF ||
                       c >= 0x3040 && c <= 0x30FF || c >= 0xAC00 && c <= 0xD7AF;
            if (!cjk) return false;
        }
        return token.Length > 0;
    }

    /// <summary>二值化 + 列投影 + 空白 gap 切分，返回墨水段 [startX, endX)（位图局部坐标）。</summary>
    private static List<(int StartX, int EndX)> ExtractInkSegments(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return AnalyzeColumns(bytes, data.Stride, w, h);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static List<(int StartX, int EndX)> AnalyzeColumns(byte[] bytes, int stride, int w, int h)
    {
        // 单遍统计：灰度和、暗像素数、每列灰度值（第二遍按阈值计数）
        long graySum = 0;
        int totalPixels = w * h;
        int darkCount = 0;
        var grays = new byte[totalPixels];

        for (int y = 0; y < h; y++)
        {
            int rowBase = y * stride;
            for (int x = 0; x < w; x++)
            {
                byte b = bytes[rowBase + x * 4];
                byte g = bytes[rowBase + x * 4 + 1];
                byte r = bytes[rowBase + x * 4 + 2];
                int gray = (r * 299 + g * 587 + b * 114) / 1000;
                grays[y * w + x] = (byte)gray;
                graySum += gray;
                if (gray < 128) darkCount++;
            }
        }

        // 极性检测：暗像素过半 → 暗色主题（暗底亮字），反转前景定义
        bool darkBackground = darkCount * 2 > totalPixels;
        double meanGray = (double)graySum / totalPixels;
        // 阈值 = 均值 × 0.6：文字是少数高对比像素，背景主导均值
        int threshold = Math.Clamp((int)Math.Round(meanGray * 0.6), 8, 247);

        var colInk = new int[w];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int gray = grays[rowBase + x];
                bool isInk = darkBackground ? gray >= 256 - threshold : gray <= threshold;
                if (isInk) colInk[x]++;
            }
        }

        return BuildSegmentsFromInk(colInk, h);
    }

    private static List<(int StartX, int EndX)> BuildSegmentsFromInk(int[] colInk, int height)
    {
        int noiseFloor = Math.Max(1, (int)Math.Round(height * InkNoiseFactor));
        int gapThreshold = Math.Max(GapMinPx, (int)Math.Round(height * GapHeightFactor));

        bool HasInk(int x) => colInk[x] >= noiseFloor;

        var segments = new List<(int, int)>();
        int segStart = -1;
        int runEmpty = 0;

        for (int x = 0; x < colInk.Length; x++)
        {
            if (HasInk(x))
            {
                if (segStart < 0) segStart = x;
                runEmpty = 0;
            }
            else if (segStart >= 0)
            {
                runEmpty++;
                // 连续空白达到词间距阈值 → 结束当前段
                if (runEmpty >= gapThreshold)
                {
                    int endX = x - runEmpty + 1;
                    if (endX - segStart >= MinSegmentWidth)
                        segments.Add((segStart, endX));
                    segStart = -1;
                    runEmpty = 0;
                }
            }
        }

        if (segStart >= 0)
        {
            int endX = colInk.Length - runEmpty;
            if (endX - segStart >= MinSegmentWidth)
                segments.Add((segStart, endX));
        }

        return segments;
    }
}
