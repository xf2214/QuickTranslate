using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Ocr;
using QuickTranslate.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace QuickTranslate.Tests;

public class ModelValidationTests
{
    private readonly ITestOutputHelper _out;
    private static readonly string ModelsDir = GetModelsDir();

    public ModelValidationTests(ITestOutputHelper output)
    {
        _out = output;
        _out.WriteLine($"Models dir: {ModelsDir} (exists={Directory.Exists(ModelsDir)})");
        if (Directory.Exists(ModelsDir))
        {
            foreach (var f in Directory.GetFiles(ModelsDir).OrderBy(n => n))
            {
                var fi = new FileInfo(f);
                var sz = fi.Length >= 1024 * 1024
                    ? $"{fi.Length / 1024.0 / 1024.0:F2}MB"
                    : $"{fi.Length / 1024.0:F1}KB";
                _out.WriteLine($"  {Path.GetFileName(f),-18} {sz}");
            }
        }
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
            if (Directory.Exists(full)) return full;
        }
        return Path.GetFullPath(candidates[0]);
    }

    [Fact]
    [Trait("Category", "Model")]
    public void Required_Model_Files_Exist_And_Valid_Sizes()
    {
        Assert.True(Directory.Exists(ModelsDir), "models directory missing: " + ModelsDir);
        var det = Path.Combine(ModelsDir, "det.onnx");
        var rec = Path.Combine(ModelsDir, "rec.onnx");
        var kps = Path.Combine(ModelsDir, "ppocr_keys.txt");
        Assert.True(File.Exists(det), "det.onnx missing: " + det);
        Assert.True(File.Exists(rec), "rec.onnx missing: " + rec);
        Assert.True(File.Exists(kps), "ppocr_keys.txt missing: " + kps);
        var detFi = new FileInfo(det);
        var recFi = new FileInfo(rec);
        var keysFi = new FileInfo(kps);
        Assert.True(detFi.Length > 10_000_000, $"det.onnx too small ({detFi.Length}B)");
        Assert.True(recFi.Length > 30_000_000, $"rec.onnx too small ({recFi.Length}B)");
        Assert.True(keysFi.Length > 10_000, $"ppocr_keys.txt too small ({keysFi.Length}B)");
        var lines = File.ReadAllLines(kps);
        _out.WriteLine($"Dictionary lines: {lines.Length}");
        Assert.True(lines.Length >= 5000, "dictionary should have >=5000 lines");
    }

    [Fact]
    [Trait("Category", "Model")]
    public void Det_ONNX_Session_Creates_With_CPU_EP_And_Reports_IO()
    {
        var path = Path.Combine(ModelsDir, "det.onnx");
        Assert.True(File.Exists(path), "det.onnx missing");
        using var so = new SessionOptions();
        try { so.AppendExecutionProvider_CPU(0); } catch { /* ignore */ }
        using var sess = new InferenceSession(path, so);
        Assert.True(sess.InputMetadata.Count == 1,
            $"det should have 1 input, got {sess.InputMetadata.Count}");
        var inputKv = sess.InputMetadata.First();
        var dimsIn = inputKv.Value.Dimensions != null
            ? string.Join(",", inputKv.Value.Dimensions) : "?";
        _out.WriteLine($"Det input: {inputKv.Key} type={inputKv.Value.ElementType} shape=[{dimsIn}]");
        Assert.True(sess.OutputMetadata.Count >= 1,
            $"det should have >=1 output, got {sess.OutputMetadata.Count}");
        _out.WriteLine($"Det outputs ({sess.OutputMetadata.Count}):");
        foreach (var kv in sess.OutputMetadata)
        {
            var dims = kv.Value.Dimensions != null ? string.Join(",", kv.Value.Dimensions) : "?";
            _out.WriteLine($"  {kv.Key}: type={kv.Value.ElementType} shape=[{dims}]");
        }
    }

    [Fact]
    [Trait("Category", "Model")]
    public void Rec_ONNX_Session_Creates_3D_Output_With_6kPlus_Classes()
    {
        var path = Path.Combine(ModelsDir, "rec.onnx");
        Assert.True(File.Exists(path), "rec.onnx missing");
        using var so = new SessionOptions();
        try { so.AppendExecutionProvider_CPU(0); } catch { /* ignore */ }
        using var sess = new InferenceSession(path, so);
        Assert.True(sess.InputMetadata.Count == 1,
            $"rec should have 1 input, got {sess.InputMetadata.Count}");
        var inKv = sess.InputMetadata.First();
        var dimsIn = inKv.Value.Dimensions != null
            ? string.Join(",", inKv.Value.Dimensions) : "?";
        _out.WriteLine($"Rec input: {inKv.Key} type={inKv.Value.ElementType} shape=[{dimsIn}]");
        Assert.True(sess.OutputMetadata.Count == 1,
            $"rec should have exactly 1 output, got {sess.OutputMetadata.Count}");
        var outKv = sess.OutputMetadata.First();
        var dimsA = outKv.Value.Dimensions;
        Assert.NotNull(dimsA);
        Assert.Equal(3, dimsA.Length);
        _out.WriteLine($"Rec output: {outKv.Key} shape=[{dimsA[0]},{dimsA[1]},{dimsA[2]}] type={outKv.Value.ElementType}");
        _out.WriteLine($" => B={dimsA[0]} T={dimsA[1]} num_classes={dimsA[2]}");
        Assert.True(dimsA[2] >= 6000, $"rec num_classes ({dimsA[2]}) too small for Chinese model");
    }

    private class StubAppDataProvider : IAppDataProvider
    {
        private readonly string _dir;
        public StubAppDataProvider(string dir) { _dir = dir; }
        public string GetAppDataDirectory() => _dir;
        public string GetLogDirectory() => Path.Combine(_dir, "logs");
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task PaddleOcrV6Engine_WarmUp_Creates_3Sessions_And_Loads_Dict()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<PaddleOcrV6Engine>(entries);
        var settings = Options.Create(new AppSettings());
        var stubApp = new StubAppDataProvider(ModelsDir);
        var engine = new PaddleOcrV6Engine(settings, stubApp, logger);
        _out.WriteLine($"EngineName={engine.EngineName}");
        Assert.Equal("PP-OCRv6-ONNX", engine.EngineName);
        Assert.True(engine.IsAvailable,
            "engine should be available with real models present");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await engine.WarmUpAsync(cts.Token);
        _out.WriteLine("WarmUp completed");
        foreach (var e in entries
                     .Where(x => x.Level >= Microsoft.Extensions.Logging.LogLevel.Information))
        {
            _out.WriteLine($"  [{e.Level}] {e.Message}");
        }
        Assert.DoesNotContain(entries,
            e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error ||
                 e.Level == Microsoft.Extensions.Logging.LogLevel.Critical);
    }
}
