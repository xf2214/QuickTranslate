using System.Drawing;
using System.Drawing.Imaging;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Infrastructure.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

/// <summary>
/// OCR 准确性增强的单元测试：
/// CTC 行级置信度 / rec 输入增强（灰度去彩边 + 深色反色）/
/// det 后处理（闭运算桥接断笔 + 行高按自身比例扩展）/ 置信度跨清洗器传播。
/// </summary>
public class OcrAccuracyEnhancementTests
{
    // ==================== CTC 置信度 ====================

    private static void SetRow(float[] probs, int t, int C, int bestIdx, float bestVal)
    {
        // 剩余概率均摊，保证行和 = 1（模拟图内含 softmax 的归一化输出）
        float rest = (1f - bestVal) / (C - 1);
        for (int c = 0; c < C; c++) probs[t * C + c] = c == bestIdx ? bestVal : rest;
    }

    [Fact]
    public void CtcGreedyDecode_NormalizedProbs_TextAndMeanConfidence()
    {
        // dict 布局：blank, 'a', 'b', 空格。序列 a, a(重复合并), blank, b → "ab"
        var dict = new[] { "", "a", "b", " " };
        int C = 4, T = 4;
        var probs = new float[T * C];
        SetRow(probs, 0, C, 1, 0.9f); // a
        SetRow(probs, 1, C, 1, 0.9f); // a 重复（CTC 合并，不计入置信度）
        SetRow(probs, 2, C, 0, 0.9f); // blank
        SetRow(probs, 3, C, 2, 0.8f); // b

        var (text, conf) = PaddleOcrV6Engine.CtcGreedyDecode(probs, T, C, dict);

        Assert.Equal("ab", text);
        Assert.Equal(0.85f, conf, 3); // (0.9 + 0.8) / 2
    }

    [Fact]
    public void CtcGreedyDecode_AllBlank_EmptyTextZeroConfidence()
    {
        var dict = new[] { "", "a", "b", " " };
        int C = 4, T = 3;
        var probs = new float[T * C];
        for (int t = 0; t < T; t++) SetRow(probs, t, C, 0, 0.95f);

        var (text, conf) = PaddleOcrV6Engine.CtcGreedyDecode(probs, T, C, dict);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0f, conf);
    }

    [Fact]
    public void CtcGreedyDecode_RawLogits_SoftmaxNormalizedConfidence()
    {
        // 行和 ≠ 1 的原始 logits：置信度应按时间步 softmax 归一后仍落在 [0,1]
        var dict = new[] { "", "a", " " };
        int C = 3, T = 2;
        var probs = new float[T * C];
        probs[0 * C + 1] = 10f; probs[0 * C + 0] = 1f; probs[0 * C + 2] = 1f;
        probs[1 * C + 1] = 10f; probs[1 * C + 0] = 1f; probs[1 * C + 2] = 1f;

        var (text, conf) = PaddleOcrV6Engine.CtcGreedyDecode(probs, T, C, dict);

        Assert.Equal("a", text); // t=1 与 t=0 同类重复合并
        Assert.InRange(conf, 0.99f, 1.0f); // softmax(10 vs 1,1) ≈ 0.9997
    }

    // ==================== rec 输入增强（灰度 + 深色反色） ====================

    [Fact]
    public void EnhanceForRec_DarkLine_InvertedToDarkTextOnLight()
    {
        using var src = new Bitmap(20, 10, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.FromArgb(30, 30, 30));          // 暗色主题背景
            g.FillRectangle(Brushes.White, 4, 2, 12, 6);  // 白色文字块
        }

        using var dst = PaddleOcrV6Engine.EnhanceForRec(src, out bool inverted);

        Assert.True(inverted);
        Assert.True(dst.GetPixel(0, 0).R > 220, "背景应反色成浅色");
        Assert.True(dst.GetPixel(10, 5).R < 30, "文字应反色成深色");
        Assert.Equal(dst.GetPixel(10, 5).R, dst.GetPixel(10, 5).G); // 灰度：R=G=B
    }

    [Fact]
    public void EnhanceForRec_LightLine_GrayscaleWithoutInversion()
    {
        using var src = new Bitmap(20, 10, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 4, 2, 12, 6);
        }

        using var dst = PaddleOcrV6Engine.EnhanceForRec(src, out bool inverted);

        Assert.False(inverted);
        Assert.True(dst.GetPixel(0, 0).R > 240);
        Assert.True(dst.GetPixel(10, 5).R < 30);
    }

    // ==================== det 后处理：闭运算与行高扩展 ====================

    private static float[] BuildPredMap(int w, int h, params (int X, int Y, int W, int H, float Val)[] blobs)
    {
        var map = new float[w * h];
        foreach (var (x, y, bw, bh, v) in blobs)
            for (int yy = y; yy < y + bh; yy++)
                for (int xx = x; xx < x + bw; xx++)
                    map[yy * w + xx] = v;
        return map;
    }

    [Fact]
    public void DbPostprocess_StrokeBreakBeyondMergeHeuristic_BridgedByClosing()
    {
        // 两碎片垂直重叠仅 2/10（< 合并启发式的 0.3 阈值），
        // 只有 mask 级闭运算能桥接 → 单盒证明闭运算生效
        int w = 200, h = 80;
        var map = BuildPredMap(w, h,
            (20, 25, 40, 10, 0.9f),
            (50, 33, 40, 10, 0.9f));

        var boxes = PaddleOcrV6Engine.DbPostprocess(map, w, h, w, h, 1f, 1f, w, h);

        Assert.Single(boxes);
    }

    [Fact]
    public void DbPostprocess_DistantColumns_RemainSeparate()
    {
        // 相距 150px 的两段文字：闭运算不桥接、合并启发式不吸收
        int w = 400, h = 80;
        var map = BuildPredMap(w, h,
            (20, 30, 80, 12, 0.9f),
            (250, 30, 80, 12, 0.9f));

        var boxes = PaddleOcrV6Engine.DbPostprocess(map, w, h, w, h, 1f, 1f, w, h);

        Assert.Equal(2, boxes.Count);
    }

    [Fact]
    public void DbPostprocess_SmallFontBox_NotInflatedToFrameRatio()
    {
        // 高帧中的小字行：旧实现按 6% 帧高强制 clamp（1200px 帧 → 至少 43px），
        // 新实现按盒子自身高度比例扩展，小字不被硬撑高（crop 不会吃进相邻行）
        int w = 300, h = 1200;
        var map = BuildPredMap(w, h, (40, 600, 120, 8, 0.9f));

        var boxes = PaddleOcrV6Engine.DbPostprocess(map, w, h, w, h, 1f, 1f, w, h);

        var box = Assert.Single(boxes);
        int oldMinH = Math.Max(18, (int)Math.Round(h * 0.06 * 0.6)); // 旧下限 = 43
        Assert.True(box.Height < oldMinH,
            $"盒高 {box.Height} 应保持比例扩展，不应被撑到旧帧高比例下限 {oldMinH}");
        Assert.True(box.Height >= 8, "盒高至少覆盖原始笔画");
    }

    [Fact]
    public void CloseMask_BridgesTwoPixelGap_KeepsLargeGap()
    {
        // 单行 mask：[XX__XX]（2px 缝应桥接）与 [XX____XX]（4px 缝保留）
        int w = 20, h = 3;
        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            mask[y * w + 0] = 1; mask[y * w + 1] = 1;
            mask[y * w + 4] = 1; mask[y * w + 5] = 1;
            mask[y * w + 10] = 1; mask[y * w + 11] = 1;
            mask[y * w + 16] = 1; mask[y * w + 17] = 1;
        }

        var closed = PaddleOcrV6Engine.CloseMask(mask, w, h);

        Assert.Equal(1, closed[1 * w + 2]);  // 2px 缝被桥接
        Assert.Equal(1, closed[1 * w + 3]);
        Assert.Equal(0, closed[1 * w + 13]); // 4px 缝保留
        Assert.Equal(0, closed[1 * w + 14]);
    }

    // ==================== 置信度跨清洗器传播 ====================

    [Fact]
    public void LineGapSplitter_PreservesLineConfidence()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 100, 30), "left", 0.9f, 0),
            new OcrWord(new PhysicalRect(300, 0, 100, 30), "right", 0.9f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 400, 30), words, "left right", null, 0.77f);

        var parts = LineGapSplitter.SplitLines(new[] { line });

        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(0.77f, p.Confidence));
    }

    [Fact]
    public void LeadingGlyphCleaner_PreservesLineConfidence()
    {
        var words = new[]
        {
            new OcrWord(new PhysicalRect(0, 0, 12, 30), "?", 0.9f, 0),
            new OcrWord(new PhysicalRect(40, 0, 120, 30), "hello", 0.9f, 0)
        };
        var line = new OcrLine(new PhysicalRect(0, 0, 200, 30), words, "? hello", null, 0.66f);

        var cleaned = LeadingGlyphCleaner.Clean(new[] { line }, out int cleanedCount);

        Assert.Equal(1, cleanedCount);
        Assert.Equal("hello", cleaned[0].Text);
        Assert.Equal(0.66f, cleaned[0].Confidence);
    }
}
