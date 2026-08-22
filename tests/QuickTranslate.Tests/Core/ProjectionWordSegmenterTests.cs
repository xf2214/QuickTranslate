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
        // Y/Height 垂直收紧：框落在行框内且明显小于整行高（不含行框留白）
        Assert.True(words[0].Box.Y > frameRegion.Y + localBox.Y,
            "收紧后框顶应低于行框顶（去掉留白）");
        Assert.True(words[0].Box.Height < localBox.Height,
            "收紧后框高应小于整行高");
        Assert.True(words[0].Box.Y + words[0].Box.Height < frameRegion.Y + localBox.Y + localBox.Height);
        Assert.Equal(2, words[0].LineIndex);
        // 投影精确切分置信度
        Assert.Equal(0.9f, words[0].Confidence);
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
        // 坐标换算到屏幕绝对坐标；垂直收紧后框顶低于行框顶、框在行内
        Assert.True(words[0].Box.X >= frameRegion.X);
        Assert.True(words[1].Box.Right <= frameRegion.X + localBox.Width);
        Assert.True(words[0].Box.Y >= frameRegion.Y);
        Assert.True(words[0].Box.Y + words[0].Box.Height <= frameRegion.Y + localBox.Height);
    }

    [Fact]
    public void Case10_TightSpacing_ConstrainedSplitsTouchingBlocks()
    {
        // 两个实心墨块紧贴（无间隙）→ 投影只能切出 1 段，TrySegment 失败；
        // TrySegmentConstrained 用 DP 在同代价切点中按期望宽度择优：
        // token 长度 2:5 与墨块宽度 40:100 成比例，切点应落在交界处 x=70。
        var bmp = new Bitmap(200, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 30, 10, 40, 24);   // "aa"（列 30..69）
            g.FillRectangle(brush, 70, 10, 100, 24);  // "bbbbb"（列 70..169，紧贴前者）
        }
        var frameRegion = new PhysicalRect(0, 0, 200, 44);
        var localBox = new PhysicalRect(0, 0, 200, 44);

        bool exact = ProjectionWordSegmenter.TrySegment(bmp, "aa bbbbb", localBox, frameRegion, false, 0, out _);
        Assert.False(exact);

        bool ok = ProjectionWordSegmenter.TrySegmentConstrained(bmp, "aa bbbbb", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("aa", words[0].Text);
        Assert.Equal("bbbbb", words[1].Text);
        // 切点应落在两墨块交界处附近（x=70 ± 6px）
        Assert.True(Math.Abs(words[0].Box.Right - 70) <= 6,
            $"第一段右缘 {words[0].Box.Right} 应贴近交界列 70");
        // 受约束切分置信度低于投影精确切分
        Assert.Equal(0.8f, words[0].Confidence);
    }

    [Fact]
    public void Case11_PseudoSplit_MergeRepairSucceeds()
    {
        // 伪分裂："aa" 墨块内部有 10px 缝隙（大于 gap 阈值被误切成两段），
        // 但与 "bb" 之间的 31px 才是真正词间距 → 投影切出 3 段，
        // 合并修复按最小间隔优先合并（10px < 31px）后对齐 2 个 token。
        var bmp = new Bitmap(200, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 30, 10, 19, 24);   // "aa" 左半（列 30..48）
            g.FillRectangle(brush, 59, 10, 20, 24);   // "aa" 右半（列 59..78，10px 伪缝）
            g.FillRectangle(brush, 110, 10, 60, 24);  // "bb"（列 110..169，31px 词间距）
        }
        var frameRegion = new PhysicalRect(0, 0, 200, 44);
        var localBox = new PhysicalRect(0, 0, 200, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "aa bb", localBox, frameRegion, false, 0, out var words, out var detail);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("aa", words[0].Text);
        Assert.Equal("bb", words[1].Text);
        // 合并后的 "aa" 框应横跨两块（30..79 附近）
        Assert.True(words[0].Box.X <= 32 && words[0].Box.Right >= 76,
            $"合并后 aa 框 ({words[0].Box.X}..{words[0].Box.Right}) 应横跨两块");
        Assert.Contains("merge", detail);
    }

    [Fact]
    public void Case12_VerticalTighten_BoxMatchesInkRange()
    {
        // 单墨块位于行 12..26 → 收紧后框高 = 墨水范围 + 上下各 1px padding
        var bmp = new Bitmap(120, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 20, 12, 60, 14); // 墨块行 12..25（含）
        }
        var frameRegion = new PhysicalRect(10, 50, 120, 44);
        var localBox = new PhysicalRect(0, 0, 120, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "Hi", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal(frameRegion.Y + 11, words[0].Box.Y);   // inkTop(12) - 1
        Assert.Equal(16, words[0].Box.Height);              // (26) - 11 + 1 = 14 + 2 padding
    }

    [Fact]
    public void Case13_TrailingPunctuation_SplitIntoSeparateRun()
    {
        // 代码场景：标识符紧贴分号（"MinSegmentWidth;"）是一个 token，
        // 应按脚本/标点边界拆成独立词框，选取框不再覆盖标点。
        var (bmp, spans) = DrawWords(new[] { "MinSegmentWidth;" }, height: 44);
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "MinSegmentWidth;", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("MinSegmentWidth", words[0].Text);
        Assert.Equal(";", words[1].Text);
        // 标识符框不应覆盖到分号：右缘明显小于整段实测右缘
        int fullRight = frameRegion.X + spans[0].Right;
        Assert.True(words[0].Box.Right < fullRight - 2,
            $"标识符右缘 {words[0].Box.Right} 应小于整段右缘 {fullRight}");
        Assert.True(words[1].Box.Left >= words[0].Box.Right);
    }

    [Fact]
    public void Case14_ApostropheWord_NotSplit()
    {
        // don't 中的撇号不应拆分，仍是一个词
        var (bmp, _) = DrawWords(new[] { "don't" }, height: 44);
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "don't", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal("don't", words[0].Text);
    }

    [Fact]
    public void Case15_NarrowSegment_RunSplitDoesNotThrow()
    {
        // 回归：段宽比 run 数还窄（如 3px 墨水、五个 run）时，
        // 旧实现 RefineCut 会因 lo>hi 抛 ArgumentException 击穿整个 OCR 管线；
        // 新实现应退化为均分，不抛异常。
        var bmp = new Bitmap(60, 20, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 20, 5, 3, 10); // 3px 宽墨块
        }
        var frameRegion = new PhysicalRect(0, 0, 60, 20);
        var localBox = new PhysicalRect(0, 0, 60, 20);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "a;b;c", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(5, words.Count);
        Assert.Equal("a", words[0].Text);
        Assert.Equal(";", words[1].Text);
    }

    [Fact]
    public void Case16_FusedToken_DroppedSpace_SplitIntoTwoWords()
    // 粘连词修复：rec 丢空格时 "voidMain" 是一个 token，投影段横跨两词。
    // 段内存在达到词间距级别的单一缝隙（5px ≥ minGap）→ 拆成两个词框，
    // 选框不再横跨两词，翻译拿到真实单词。
    {
        var bmp = new Bitmap(200, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 20, 10, 40, 24);   // "void" 列 20..59
            g.FillRectangle(brush, 65, 10, 40, 24);   // "Main" 列 65..104，中间 5px 缝隙
        }
        var frameRegion = new PhysicalRect(0, 0, 200, 44);
        var localBox = new PhysicalRect(0, 0, 200, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "voidMain", localBox, frameRegion, false, 0, out var words, out var detail);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("void", words[0].Text);
        Assert.Equal("Main", words[1].Text);
        // 两个词框各自贴合自己的墨块，不再横跨
        Assert.True(words[0].Box.Right <= 62, $"void 框右缘 {words[0].Box.Right} 应贴左墨块");
        Assert.True(words[1].Box.X >= 63, $"Main 框左缘 {words[1].Box.X} 应贴右墨块");
        Assert.Contains("unfuse", detail);
    }

    [Fact]
    public void Case17_FusedToken_RealisticRendering_SplitAtWordGap()
    // 真实渲染："void"/"Main" 用 5px 词间距绘制（低于投影 gap 阈值 7px，
    // 模拟 rec 丢空格），字母间距只有 0-2px → 只在词间隙处拆分。
    {
        var (bmp, spans) = DrawWords(new[] { "void", "Main" }, height: 44, gap: 5);
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, bmp.Height);
        var localBox = new PhysicalRect(0, 0, bmp.Width, bmp.Height);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "voidMain", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("void", words[0].Text);
        Assert.Equal("Main", words[1].Text);
        // 词框中心应贴合各自实测渲染位置（误差 ≤ 6px）
        int c0 = (spans[0].Left + spans[0].Right) / 2;
        int c1 = (spans[1].Left + spans[1].Right) / 2;
        Assert.True(Math.Abs(words[0].Box.X + words[0].Box.Width / 2 - c0) <= 6);
        Assert.True(Math.Abs(words[1].Box.X + words[1].Box.Width / 2 - c1) <= 6);
    }

    [Fact]
    public void Case18_JustifiedWideLetterGaps_NotSplit()
    // 两端对齐排版：单词 "word" 内部字母缝隘普遍拉宽到 4px（均等、无突出大缝）
    // → 不能把正常单词拦腰切断，仍输出整词。
    {
        var bmp = new Bitmap(120, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 20, 10, 10, 24);   // w 列 20..29
            g.FillRectangle(brush, 34, 10, 10, 24);   // o 列 34..43（4px 缝）
            g.FillRectangle(brush, 48, 10, 10, 24);   // r 列 48..57（4px 缝）
            g.FillRectangle(brush, 62, 10, 10, 24);   // d 列 62..71（4px 缝）
        }
        var frameRegion = new PhysicalRect(0, 0, 120, 44);
        var localBox = new PhysicalRect(0, 0, 120, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "word", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal("word", words[0].Text);
    }

    [Fact]
    public void Case19_ConstrainedDP_CutsAtWidestZeroGap()
    // 受约束切分平局择优：三个等宽墨块、两条零墨缝隙（1px / 3px），
    // 两个 token → DP 只能合并两个墨块，切点应落在更宽的缝隙（真实词间隙），
    // 而不是仅靠宽度启发式落在窄缝。
    {
        var bmp = new Bitmap(200, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 20, 10, 40, 24);   // A 列 20..59
            g.FillRectangle(brush, 61, 10, 40, 24);   // B 列 61..100（1px 窄缝）
            g.FillRectangle(brush, 104, 10, 40, 24);  // C 列 104..143（3px 宽缝）
        }
        var frameRegion = new PhysicalRect(0, 0, 200, 44);
        var localBox = new PhysicalRect(0, 0, 200, 44);

        bool ok = ProjectionWordSegmenter.TrySegmentOrConstrained(bmp, "aa bb", localBox, frameRegion, false, 0, out var words, out _);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        // 切点在宽缝（列 101..103）：第二个词框左缘应 ≥101，而非窄缝的 60
        Assert.True(words[1].Box.X >= 101,
            $"第二个词框 [{words[1].Box.X}..{words[1].Box.Right}] text='{words[1].Text}'，第一个 [{words[0].Box.X}..{words[0].Box.Right}] text='{words[0].Text}'，应落在宽缝处（≥101）");
    }

    [Fact]
    public void Case20_VerticalTighten_NeighborBandExcluded()
    // det 框被高度归一化撑大后，邻行墨水渗入裁剪图（上边缘小墨带）：
    // 垂直收紧应只保留墨量最大的主墨水带，词框不连带上边缘的邻行内容。
    {
        var bmp = new Bitmap(120, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 20, 2, 20, 4);   // 邻行渗漏带 行 2..5（墨量小）
            g.FillRectangle(brush, 20, 12, 60, 14); // 主墨块行 12..25（含）
        }
        var frameRegion = new PhysicalRect(10, 50, 120, 44);
        var localBox = new PhysicalRect(0, 0, 120, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "Hi", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal(frameRegion.Y + 11, words[0].Box.Y);   // 主带 inkTop(12) - 1，不含邻行带
        Assert.Equal(16, words[0].Box.Height);              // 只覆盖主带 + 上下各 1px padding
    }

    [Fact]
    public void Case21_OverSplit_MergesByAlignment_NotSmallestGap()
    // 过度分裂修复："aa" 被宽字距（10px）分成两块，而与 "bb" 的词间空隙更窄（7px）。
    // 旧最小空隙合并会在词间隙合并 → "bb" 的框吸入 "aa" 后半墨水
    // （选框连通到前面的内容且偏离光标）。宽度感知合并应在词内宽缝合并。
    {
        var bmp = new Bitmap(130, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 10, 12, 20, 14); // "aa" 前半 10..29
            g.FillRectangle(brush, 40, 12, 20, 14); // "aa" 后半 40..59（字距空隙 10px）
            g.FillRectangle(brush, 67, 12, 40, 14); // "bb" 67..106（词间空隙 7px）
        }
        var frameRegion = new PhysicalRect(0, 0, 130, 44);
        var localBox = new PhysicalRect(0, 0, 130, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "aa bb", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("aa", words[0].Text);
        Assert.Equal("bb", words[1].Text);
        // "aa" 框覆盖自身两块墨水；"bb" 框不吸入 "aa" 后半（左缘 ≥ 67，旧行为会给出 40）
        Assert.True(words[0].Box.Right >= 60, $"aa 框右缘 {words[0].Box.Right} 应覆盖到 60");
        Assert.True(words[1].Box.X >= 67, $"bb 框左缘 {words[1].Box.X} 不应吸入前词墨水");
    }

    [Fact]
    public void Case22_WrongGapDirectAlignment_RejectedAndRetried()
    // 阈值恰在错误空隙切开且段数恰好等于 token 数时（"aa" 后半与 "bb" 被并入同段），
    // 旧逻辑直接返回错位框；宽度对齐校验应拒绝并经放宽阈值重试后正确对齐。
    {
        var bmp = new Bitmap(130, 44, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, 10, 12, 20, 14); // "aa" 前半 10..29
            g.FillRectangle(brush, 38, 12, 20, 14); // "aa" 后半 38..57（字距空隙 8px，首选阈值会切开）
            g.FillRectangle(brush, 64, 12, 55, 14); // "bb" 64..118（词间空隙 6px，首选阈值不切）
        }
        var frameRegion = new PhysicalRect(0, 0, 130, 44);
        var localBox = new PhysicalRect(0, 0, 130, 44);

        bool ok = ProjectionWordSegmenter.TrySegment(bmp, "aa bb", localBox, frameRegion, false, 0, out var words);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("aa", words[0].Text);
        Assert.Equal("bb", words[1].Text);
        // "aa" 框覆盖到后半墨水；"bb" 框左缘 ≥ 64（旧行为会给出 38，连通到前面的内容）
        Assert.True(words[0].Box.Right >= 58, $"aa 框右缘 {words[0].Box.Right} 应覆盖到 58");
        Assert.True(words[1].Box.X >= 64, $"bb 框左缘 {words[1].Box.X} 不应吸入前词墨水");
    }

    // ===== 粘连词拆分的词汇佐证回归（修复"同一单词选区中断"，如 commit 只选中 com） =====

    /// <summary>绘制逐字母墨块：widths 为各字母块宽，gaps 为字母间空隙宽（gaps.Length == widths.Length - 1）。</summary>
    private (Bitmap Bmp, List<(int Left, int Right)> Blocks) DrawLetterBlocks(int[] widths, int[] gaps, int height = 44)
    {
        int width = widths.Sum() + gaps.Sum() + 20;
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _bitmaps.Add(bmp);
        var blocks = new List<(int, int)>();
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            int x = 10;
            for (int i = 0; i < widths.Length; i++)
            {
                g.FillRectangle(brush, x, 10, widths[i], 24);
                blocks.Add((x, x + widths[i]));
                x += widths[i];
                if (i < gaps.Length) x += gaps[i];
            }
        }
        return (bmp, blocks);
    }

    [Fact]
    public void Case23_KerningGapInsideWord_PlausibleTokenNotSplit()
    // 回归（用户报告：commit 选区止于 com）：正常单词内部出现单一异常宽的字距缝时，
    // 旧逻辑按"丢空格"把词拦腰切断（com|mit）。token 本身是合理词（词典命中）时
    // 必须保持整词输出——纯几何无法区分字距缝与丢空格，需词汇佐证裁决。
    {
        // c-o-m | m-i-t：字母缝 1px，"com" 与 "mit" 之间单一 5px 干净缝隙（≥ minGap=4@h44）
        var (bmp, blocks) = DrawLetterBlocks(
            new[] { 10, 10, 10, 10, 10, 10 }, new[] { 1, 1, 5, 1, 1 });
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, 44);
        var localBox = new PhysicalRect(0, 0, bmp.Width, 44);

        bool ok = ProjectionWordSegmenter.TrySegmentOrConstrained(
            bmp, "commit", localBox, frameRegion, false, 0,
            isPlausibleWord: t => string.Equals(t, "commit", StringComparison.OrdinalIgnoreCase),
            out var words, out _);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal("commit", words[0].Text);
        // 框横跨全部六个字母块（不被截断到 com）
        Assert.True(words[0].Box.X <= blocks[0].Left + 1 && words[0].Box.Right >= blocks[^1].Right - 1,
            $"commit 框 [{words[0].Box.X}..{words[0].Box.Right}] 应覆盖整词 [{blocks[0].Left}..{blocks[^1].Right}]");
    }

    [Fact]
    public void Case24_FusedToken_WithPlausibility_StillSplitsWhenPiecesAreWords()
    // 特性保留：真正的丢空格融合词（voidMain）在词汇佐证下仍应拆分——
    // token 非合理词且所有片段均为合理词。
    {
        var (bmp, _) = DrawLetterBlocks(new[] { 40, 40 }, new[] { 5 });   // void | Main，5px 词缝
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, 44);
        var localBox = new PhysicalRect(0, 0, bmp.Width, 44);

        bool ok = ProjectionWordSegmenter.TrySegmentOrConstrained(
            bmp, "voidMain", localBox, frameRegion, false, 0,
            isPlausibleWord: t => t.Equals("void", StringComparison.OrdinalIgnoreCase)
                || t.Equals("main", StringComparison.OrdinalIgnoreCase),
            out var words, out _);

        Assert.True(ok);
        Assert.Equal(2, words.Count);
        Assert.Equal("void", words[0].Text);
        Assert.Equal("Main", words[1].Text);
    }

    [Fact]
    public void Case25_DegeneratePiece_NeverSplit_EvenWithoutPlausibility()
    // 退化保护：拆出的片段只有 1 个字符（如 bug→b+ug、repor→rep+o+r 的实屏撕裂）
    // 时必须放弃拆分。无词典谓词的路径同样生效。
    {
        // b | ug：10px 宽缝触发旧逻辑单缝拆分 → 文本按宽度比例分出 "b"+"ug"
        var (bmp, blocks) = DrawLetterBlocks(new[] { 10, 18 }, new[] { 10 });
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, 44);
        var localBox = new PhysicalRect(0, 0, bmp.Width, 44);

        bool ok = ProjectionWordSegmenter.TrySegmentOrConstrained(
            bmp, "bug", localBox, frameRegion, false, 0, out var words, out _);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal("bug", words[0].Text);
        Assert.True(words[0].Box.Right >= blocks[^1].Right - 1,
            $"bug 框右缘 {words[0].Box.Right} 应覆盖完整单词");
    }

    [Fact]
    public void Case26_ImplausiblePieces_NoSplit_EvenWhenTokenImplausible()
    // 片段合理性门槛：token 不是合理词、但拆出的片段也都不是合理词 → 不拆
    //（OCR 噪声串保持整段输出，避免任意撕裂）。
    {
        var (bmp, _) = DrawLetterBlocks(new[] { 20, 20 }, new[] { 8 });   // "xq" | "zu"
        var frameRegion = new PhysicalRect(0, 0, bmp.Width, 44);
        var localBox = new PhysicalRect(0, 0, bmp.Width, 44);

        bool ok = ProjectionWordSegmenter.TrySegmentOrConstrained(
            bmp, "xqzu", localBox, frameRegion, false, 0,
            isPlausibleWord: _ => false,   // 词典全 miss
            out var words, out _);

        Assert.True(ok);
        Assert.Single(words);
        Assert.Equal("xqzu", words[0].Text);
    }
}
