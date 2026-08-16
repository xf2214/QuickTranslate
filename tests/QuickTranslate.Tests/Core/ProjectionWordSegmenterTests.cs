using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class ProjectionWordSegmenterTests : IDisposable
{
    private readonly List<Bitmap> _bitmaps = new();

    public void Dispose()
    {
        foreach (var bmp in _bitmaps) bmp.Dispose();
    }

    // ===== 绘制辅助：逐词绘制（模拟 OCR 行位图），返回每词实测 [x, right] =====

    private (Bitmap Bmp, List<(int Left, int Right)> Spans) DrawWords(
        string[] words, int height, int gap = 18, bool darkTheme = false)
    {
        using var font = new Font("Arial", 28f, FontStyle.Regular, GraphicsUnit.Pixel);
        var sizes = words.Select(w => MeasureText(w, font)).ToList();
        int width = sizes.Sum(s => (int)Math.Ceiling(s.Width)) + gap * (words.Length - 1) + 20;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(darkTheme ? Color.Black : Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var brush = new SolidBrush(darkTheme ? Color.White : Color.Black);

            var spans = new List<(int, int)>();
            float x = 10f;
            int textTop = Math.Max(0, (height - 36) / 2);
            for (int i = 0; i < words.Length; i++)
            {
                g.DrawString(words[i], font, brush, x, textTop);
                spans.Add(((int)Math.Floor(x), (int)Math.Ceiling(x + sizes[i].Width)));
                x += (int)Math.Ceiling(sizes[i].Width) + gap;
            }
            return (bmp, spans);
        }
    }

    private static SizeF MeasureText(string text, Font font)
    {
        using var measureBmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(measureBmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        return g.MeasureString(text, font);
    }

    // ===== 用例 =====

    [Fact]
    public void Case1_DifferentWordLengths_AdaptiveBoxes()
    {
        // 核心回归：比例法假设字符等宽，对 "go"/"translation" 严重偏离实测；
        // 投影法词框应贴合实测渲染位置（中心误差 ≤ 6px）。
        var (bmp, spans) = DrawWords(new[] { "go", "translation", "important" }, height: 44);
        var frameRegion = new PhysicalRect(500, 300, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "go translation important", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(3, words.Count);
        Assert.Equal("go", words[0].Text);
        Assert.Equal("translation", words[1].Text);
        Assert.Equal("important", words[2].Text);
        for (int i = 0; i < 3; i++)
        {
            // spans 是位图局部坐标，词框是屏幕绝对坐标（frameRegion + localBox + 局部段）
            double measuredCenter = frameRegion.X + localBox.X + (spans[i].Left + spans[i].Right) / 2.0;
            double boxCenter = words[i].Box.X + words[i].Box.Width / 2.0;
            Assert.True(Math.Abs(boxCenter - measuredCenter) <= 6,
                $"word '{words[i].Text}' center {boxCenter:F1} vs measured {measuredCenter:F1}");
        }
    }

    [Fact]
    public void Case2_TwoWords_OrderAndBounds()
    {
        var (bmp, _) = DrawWords(new[] { "Hello", "World" }, height: 44);
        var frameRegion = new PhysicalRect(500, 300, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(20, 10, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "Hello World", localBox, frameRegion, false, 2, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("Hello", words[0].Text);
        Assert.Equal("World", words[1].Text);
        // 两框互不重叠、顺序从左到右
        Assert.True(words[0].Box.Right <= words[1].Box.Left);
        // 坐标换算：frameRegion.X + localBox.X + 局部段坐标
        Assert.True(words[0].Box.X >= frameRegion.X + localBox.X);
        Assert.True(words[1].Box.Right <= frameRegion.X + localBox.X + bmp.Width);
        // Y/Height 沿用行框
        Assert.Equal(frameRegion.Y + localBox.Y, words[0].Box.Y);
        Assert.Equal(localBox.Height, words[0].Box.Height);
        Assert.Equal(2, words[0].LineIndex);
    }

    [Fact]
    public void Case3_DarkTheme_PolarityInversion()
    {
        var (bmp, _) = DrawWords(new[] { "Hello", "World" }, height: 44, darkTheme: true);
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "Hello World", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("Hello", words[0].Text);
        Assert.Equal("World", words[1].Text);
    }

    [Fact]
    public void Case4_SingleWord_NoGap_Succeeds()
    {
        var (bmp, _) = DrawWords(new[] { "HelloWorld" }, height: 44);
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "HelloWorld", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal("HelloWorld", words[0].Text);
    }

    [Fact]
    public void Case5_SegmentCountMismatch_ReturnsFalse()
    {
        // 文本 3 个 token，但图像只有一个墨块 → 段数不匹配 → 回退比例法
        var bmp = new Bitmap(300, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 30, 10, 240, 24);
        }
        var frameRegion = new PhysicalRect(0, 0, 300, 44);
        var localBox = new PhysicalRect(0, 0, 300, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "A B C", localBox, frameRegion, false, 0, out var words);

        Assert.False(ok);
        Assert.Empty(words);
    }

    [Fact]
    public void Case6_BlankImage_ReturnsFalse()
    {
        var bmp = new Bitmap(200, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);
        var frameRegion = new PhysicalRect(0, 0, 200, 44);
        var localBox = new PhysicalRect(0, 0, 200, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "Hello", localBox, frameRegion, false, 0, out var words);

        Assert.False(ok);
        Assert.Empty(words);
    }

    [Fact]
    public void Case7_Rotated180_MapsBackToOriginalCoords()
    {
        // 矫正图（recSource）中 A 在左、BBBBB 在右；rotated180=true 时输出框
        // 必须翻回原始（颠倒）坐标系：A 框在右、BBBBB 框在左。
        var (bmp, _) = DrawWords(new[] { "A", "BBBBB" }, height: 44);
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool okRot = ProjectionWordSegmenter.TrySegment(bmp, "A BBBBB", localBox, frameRegion, true, 0, out var rotatedWords);
        Assert.True(okRot);
        Assert.Equal("A", rotatedWords[0].Text);
        Assert.Equal("BBBBB", rotatedWords[1].Text);
        Assert.True(rotatedWords[0].Box.Left >= rotatedWords[1].Box.Right,
            "rotated180 时 A 框应翻转到 BBBBB 框右侧（原始坐标系）");

        // 对照：不旋转时 A 在左
        bool okNorm = ProjectionWordSegmenter.TrySegment(bmp, "A BBBBB", localBox, frameRegion, false, 0, out var normalWords);
        Assert.True(okNorm);
        Assert.True(normalWords[0].Box.Right <= normalWords[1].Box.Left);
    }

    [Fact]
    public void Case8_NoisePixels_Suppressed()
    {
        // 行高 80 → 噪声列阈值 = round(80×0.02) = 2：单像素噪点列（colInk=1）被抑制
        var (bmp, spans) = DrawWords(new[] { "Hello", "World" }, height: 80);
        int gapStart = spans[0].Right;
        int gapEnd = spans[1].Left;
        bmp.SetPixel((gapStart + gapEnd) / 2, 40, Color.Black);

        var frameRegion = new PhysicalRect(0, 0, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "Hello World", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("Hello", words[0].Text);
        Assert.Equal("World", words[1].Text);
    }

    [Fact]
    public void Case9_PureCjkToken_SplitPerChar()
    {
        // 纯中文 token（无空格）→ 按等宽逐字切分，框贴合单字
        var font = new Font("Microsoft YaHei", 28f, FontStyle.Regular, GraphicsUnit.Pixel);
        var bmp = new Bitmap(200, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var brush = new SolidBrush(Color.Black);
            g.DrawString("识别", font, brush, 10f, 4f);
        }

        var frameRegion = new PhysicalRect(100, 200, 200, 44);
        var localBox = new PhysicalRect(0, 0, 200, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "识别", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("识", words[0].Text);
        Assert.Equal("别", words[1].Text);
        // 两字框相邻不重叠，且宽度接近（CJK 等宽）
        Assert.True(words[0].Box.Right <= words[1].Box.Left);
        Assert.True(Math.Abs(words[0].Box.Width - words[1].Box.Width) <= 8,
            $"widths {words[0].Box.Width} vs {words[1].Box.Width}");
        // 坐标换算到屏幕绝对坐标
        Assert.True(words[0].Box.X >= frameRegion.X);
        Assert.True(words[1].Box.Right <= frameRegion.X + localBox.Width);
        Assert.Equal(frameRegion.Y, words[0].Box.Y);
    }
}
