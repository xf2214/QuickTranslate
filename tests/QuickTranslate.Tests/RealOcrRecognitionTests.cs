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

    /// <summary>用 GDI+ 渲染一行已知文本，模拟屏幕截图。</summary>
    private static ScreenFrame RenderLineFrame(string text, int fontPx = 32, int pad = 20)
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
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
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
}
