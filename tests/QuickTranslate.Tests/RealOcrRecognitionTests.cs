using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Ocr;
using QuickTranslate.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace QuickTranslate.Tests;

// 与 ModelValidationTests / OcrEngineFallbackTests 共享集合（环境变量互斥）。
// 本文件验证「真实 PP-OCRv6 模型 + 配套字典」的端到端识别正确性：
// 历史回归 = rec.onnx(18710类) 误配 ppocr_keys_v1.txt(6623行) 且
// CTC 解码 blank/空格布局写反，导致识别结果全乱码。
[Collection("OcrModelTests")]
public class RealOcrRecognitionTests
{
    private readonly ITestOutputHelper _out;
    private static readonly string ModelsDir = GetModelsDir();

    public RealOcrRecognitionTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static string GetModelsDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "assets", "models"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "assets", "models"),
            Path.Combine("assets", "models"),
            @"E:\翻译\assets\models"
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            // 目录存在还不够：只有实际包含 onnx 模型才算模型目录
            // （App 项目的 version.json 会传递复制到测试输出 assets\models\ 下，
            //  仅按目录存在判断会误命中只含 version.json 的空壳目录）
            if (Directory.Exists(full) &&
                File.Exists(Path.Combine(full, "det.onnx")) &&
                File.Exists(Path.Combine(full, "rec.onnx")))
            {
                return full;
            }
        }
        return Path.GetFullPath(candidates[0]);
    }

    private static bool ModelsPresent =>
        File.Exists(Path.Combine(ModelsDir, "det.onnx")) &&
        File.Exists(Path.Combine(ModelsDir, "rec.onnx")) &&
        File.Exists(Path.Combine(ModelsDir, "ppocr_keys.txt"));

    private class StubAppDataProvider : IAppDataProvider
    {
        private readonly string _dir;
        public StubAppDataProvider(string dir) { _dir = dir; }
        public string GetAppDataDirectory() => _dir;
        public string GetLogDirectory() => Path.Combine(_dir, "logs");
    }

    private PaddleOcrV6Engine CreateEngine()
    {
        var logger = new SpyLogger<PaddleOcrV6Engine>(new System.Collections.Generic.List<SpyLogEntry>());
        var settings = Options.Create(new AppSettings());
        var stubApp = new StubAppDataProvider(ModelsDir);
        return new PaddleOcrV6Engine(settings, stubApp, logger);
    }

    /// <summary>用 GDI+ 渲染一行已知文本，模拟屏幕截图；dark=true 模拟暗色主题（白字黑底）。</summary>
    private static ScreenFrame RenderLineFrame(string text, int fontPx = 32, int pad = 20, bool dark = false)
    {
        using var probe = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(probe))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        using var font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        SizeF size;
        using (var mg = Graphics.FromImage(probe))
        {
            size = mg.MeasureString(text, font);
        }

        int w = (int)Math.Ceiling(size.Width) + pad * 2;
        int h = (int)Math.Ceiling(size.Height) + pad * 2;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(dark ? Color.Black : Color.White);
            using var brush = new SolidBrush(dark ? Color.White : Color.Black);
            g.DrawString(text, font, brush, pad, pad);
        }
        return new ScreenFrame(bmp, new PhysicalRect(100, 100, w, h), MonitorId.Empty);
    }

    [Fact]
    public void Dictionary_Labels_Match_Rec_Output_Classes()
    {
        Assert.True(ModelsPresent, $"models not present under {ModelsDir}; skip premise broken");

        // 字典行数（v6 medium 应为 18708）+ blank + 空格 = rec 输出类别数
        var lines = File.ReadAllLines(Path.Combine(ModelsDir, "ppocr_keys.txt"));
        Assert.Equal(18708, lines.Length);

        using var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
        using var sess = new Microsoft.ML.OnnxRuntime.InferenceSession(
            Path.Combine(ModelsDir, "rec.onnx"), so);
        var dims = sess.OutputMetadata.First().Value.Dimensions;
        Assert.Equal(3, dims.Length);
        Assert.Equal(lines.Length + 2, dims[2]); // blank + chars + space
    }

    [Theory]
    [InlineData("Hello QuickTranslate", "hello quicktranslate")]
    [InlineData("translation quality", "translation quality")]
    [InlineData("中文识别测试", "中文识别测试")]
    [InlineData("The quick brown fox jumps over the lazy dog near the river bank", "quick brown fox")]
    public async Task RecognizeAsync_RenderedText_IsRecognizedCorrectly(string text, string expectedSubstring)
    {
        if (!ModelsPresent)
        {
            _out.WriteLine($"SKIP: models not present under {ModelsDir}");
            return; // CI 无模型时跳过
        }

        using var engine = CreateEngine();
        Assert.True(engine.IsAvailable, "engine should be available");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await engine.WarmUpAsync(cts.Token);

        using var frame = RenderLineFrame(text);
        var result = await engine.RecognizeAsync(frame, cts.Token);

        var recognized = string.Join(" ", result.Lines.Select(l => l.Text));
        _out.WriteLine($"Input:    '{text}'");
        _out.WriteLine($"Recognized: '{recognized}'");
        _out.WriteLine($"Lines: {result.LineCount}");

        Assert.True(result.LineCount >= 1, "should detect at least one line");
        Assert.Contains(expectedSubstring, recognized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecognizeAsync_DarkThemeLine_RecognizedWithConfidence()
    {
        if (!ModelsPresent)
        {
            _out.WriteLine($"SKIP: models not present under {ModelsDir}");
            return;
        }

        using var engine = CreateEngine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await engine.WarmUpAsync(cts.Token);

        // 暗色主题：白字黑底。引擎应先反色再进 rec，识别率与浅色路径相当，
        // 且行级置信度（CTC 平均概率）落在合理区间。
        using var frame = RenderLineFrame("dark mode text", dark: true);
        var result = await engine.RecognizeAsync(frame, cts.Token);

        var recognized = string.Join(" ", result.Lines.Select(l => l.Text));
        _out.WriteLine($"Dark theme recognized: '{recognized}'");

        Assert.True(result.LineCount >= 1, "dark line should be detected");
        Assert.Contains("dark mode", recognized, StringComparison.OrdinalIgnoreCase);

        var line = result.Lines[0];
        Assert.NotNull(line.Confidence);
        Assert.InRange(line.Confidence!.Value, 0.5f, 1.0f);
    }

    /// <summary>渲染两行间隔充分的已知文本，用于验证焦点带过滤与 det 裁剪。</summary>
    private static ScreenFrame RenderTwoLineFrame(string top, string bottom, int fontPx = 32, int pad = 20, int lineGap = 160)
    {
        using var probe = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using var font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        SizeF s1, s2;
        using (var mg = Graphics.FromImage(probe))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            s1 = mg.MeasureString(top, font);
            s2 = mg.MeasureString(bottom, font);
        }

        int lineH = (int)Math.Ceiling(Math.Max(s1.Height, s2.Height));
        int w = (int)Math.Ceiling(Math.Max(s1.Width, s2.Width)) + pad * 2;
        int h = pad + lineH + lineGap + lineH + pad;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString(top, font, brush, pad, pad);
            g.DrawString(bottom, font, brush, pad, pad + lineH + lineGap);
        }
        return new ScreenFrame(bmp, new PhysicalRect(100, 100, w, h), MonitorId.Empty);
    }

    [Fact]
    public async Task RecognizeAsync_FocusBand_OnlyBandedLine_AndDetCacheHitsOnSameCrop()
    {
        if (!ModelsPresent)
        {
            _out.WriteLine($"SKIP: models not present under {ModelsDir}");
            return;
        }

        using var engine = CreateEngine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await engine.WarmUpAsync(cts.Token);

        // 两行相距 160px：帧高 ~286，上半帧带（高 143）+ 裁剪边距 20% 帧高（57）= 200，
        // 第二行起点 ~223 完全落在裁剪区外。
        using var frame = RenderTwoLineFrame("alpha beta", "gamma delta");
        var topBand = new PhysicalRect(frame.Region.X, frame.Region.Y,
            frame.Region.Width, frame.Region.Height / 2);
        var bottomBand = new PhysicalRect(frame.Region.X, frame.Region.Y + frame.Region.Height / 2,
            frame.Region.Width, frame.Region.Height - frame.Region.Height / 2);

        var r1 = await engine.RecognizeAsync(frame, topBand, cts.Token);
        var text1 = string.Join(" ", r1.Lines.Select(l => l.Text));
        _out.WriteLine($"TopBand: '{text1}' (lines={r1.LineCount}, det={r1.Timings.Detector.TotalMilliseconds:F0}ms)");
        Assert.Equal(1, r1.LineCount);
        Assert.Contains("alpha beta", text1, StringComparison.OrdinalIgnoreCase);

        // 换到下半帧带：裁剪区不包含于首次裁剪 → 重跑 det，且只能看到第二行
        var r2 = await engine.RecognizeAsync(frame, bottomBand, cts.Token);
        var text2 = string.Join(" ", r2.Lines.Select(l => l.Text));
        _out.WriteLine($"BottomBand: '{text2}' (lines={r2.LineCount})");
        Assert.Equal(1, r2.LineCount);
        Assert.Contains("gamma delta", text2, StringComparison.OrdinalIgnoreCase);

        // 回到上半帧带：裁剪区与缓存一致 → det 缓存命中，不再推理
        var r3 = await engine.RecognizeAsync(frame, topBand, cts.Token);
        _out.WriteLine($"TopBand again: det={r3.Timings.Detector.TotalMilliseconds:F0}ms");
        Assert.Equal(1, r3.LineCount);
        Assert.True(r3.Timings.Detector < r1.Timings.Detector,
            "det cache should hit on identical crop (detector skipped)");
    }

    [Fact]
    public async Task RecognizeAsync_SameFrameWiderBand_RecLineCacheReusesRecognizedLines()
    {
        if (!ModelsPresent)
        {
            _out.WriteLine($"SKIP: models not present under {ModelsDir}");
            return;
        }

        using var engine = CreateEngine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await engine.WarmUpAsync(cts.Token);

        using var frame = RenderTwoLineFrame("alpha beta", "gamma delta");
        var topBand = new PhysicalRect(frame.Region.X, frame.Region.Y,
            frame.Region.Width, frame.Region.Height / 2);

        // 首识只带上半带：第一行被 rec 并写入行级缓存
        var r1 = await engine.RecognizeAsync(frame, topBand, cts.Token);
        Assert.Equal(1, r1.LineCount);
        Assert.Equal(0, engine.LastRecCacheHits);

        // 无带重识（模拟块生长后全带重扫）：det 重跑全帧，但第一行 box 不变 →
        // 行级缓存命中，只对第二行跑 rec
        var r2 = await engine.RecognizeAsync(frame, null, cts.Token);
        var text2 = string.Join(" ", r2.Lines.Select(l => l.Text));
        _out.WriteLine($"WiderBand: '{text2}' (lines={r2.LineCount}, recHits={engine.LastRecCacheHits}, rec={r2.Timings.Recognizer.TotalMilliseconds:F0}ms)");
        _out.WriteLine($"Box r1[0]={r1.Lines[0].Box} r2[0]={r2.Lines[0].Box}");
        Assert.Equal(2, r2.LineCount);
        Assert.True(engine.LastRecCacheHits >= 1, "first line should hit the same-frame rec line cache");
        Assert.Contains("alpha beta", text2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gamma delta", text2, StringComparison.OrdinalIgnoreCase);

        // 再次同帧重识：两行全部命中缓存 → rec 几乎零耗时，词框/文本与首识一致
        var r3 = await engine.RecognizeAsync(frame, null, cts.Token);
        _out.WriteLine($"Again: recHits={engine.LastRecCacheHits}, rec={r3.Timings.Recognizer.TotalMilliseconds:F0}ms");
        Assert.Equal(2, r3.LineCount);
        Assert.Equal(2, engine.LastRecCacheHits);
        Assert.True(r3.Timings.Recognizer < r2.Timings.Recognizer,
            "fully cached re-recognition should cost less recognizer time");
        var text3 = string.Join(" ", r3.Lines.Select(l => l.Text));
        Assert.Equal(text2, text3);
        Assert.All(r3.Lines, l => Assert.True(l.Words.Count > 0, "cached lines must carry word boxes"));
    }
}
