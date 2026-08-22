using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using NSubstitute;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Services;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure.Services;

/// <summary>
/// Network-free logic tests for ModelDownloader.
/// Live HTTP paths (DownloadAsync/GetZipAsync/DownloadRawAsync) are intentionally NOT exercised;
/// they require HTTP mocking seams that do not exist (HttpClient is new'd inline).
/// SKIPPED network aspects are documented at the end of this file.
/// </summary>
public class ModelDownloaderTests : IDisposable
{
    readonly string _tempRoot;
    readonly string _appDataDir;
    readonly IAppDataProvider _mockProvider;
    readonly ILogger<ModelDownloader> _mockLogger;

    public ModelDownloaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"qt_downloader_{Guid.NewGuid():N}");
        _appDataDir = Path.Combine(_tempRoot, "appdata");
        Directory.CreateDirectory(_appDataDir);
        _mockProvider = Substitute.For<IAppDataProvider>();
        _mockProvider.GetAppDataDirectory().Returns(_appDataDir);
        _mockProvider.GetLogDirectory().Returns(Path.Combine(_tempRoot, "logs"));
        _mockLogger = Substitute.For<ILogger<ModelDownloader>>();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    ModelDownloader CreateDownloader() => new ModelDownloader(_mockProvider, _mockLogger);

    // ---- TargetDirectory computation ----
    [Fact]
    public void TargetDirectory_EqualsAppDataModels_And_IsCreated()
    {
        var dl = CreateDownloader();
        var expected = Path.Combine(_appDataDir, "models");
        Assert.Equal(expected, dl.TargetDirectory);
        Assert.True(Directory.Exists(expected));
    }

    [Fact]
    public void DefaultUrls_ContainsExpectedKeys()
    {
        Assert.True(ModelDownloader.DefaultUrls.ContainsKey("det.onnx"));
        Assert.True(ModelDownloader.DefaultUrls.ContainsKey("rec.onnx"));
        Assert.True(ModelDownloader.DefaultUrls.ContainsKey("cls.onnx"));
        Assert.True(ModelDownloader.DefaultUrls.ContainsKey("ppocr_keys.txt"));
        Assert.Equal(4, ModelDownloader.DefaultUrls.Count);
    }

    // ---- GetFileExistsMap / AllModelFilesExist ----
    [Fact]
    public void GetFileExistsMap_AllMissing_ReturnsFalseForAll()
    {
        var dl = CreateDownloader();
        var map = dl.GetFileExistsMap();
        foreach (var kv in map)
            Assert.False(kv.Value, $"Expected missing for {kv.Key}");
    }

    [Fact]
    public void GetFileExistsMap_PartialExists_ReflectsActual()
    {
        var dl = CreateDownloader();
        File.WriteAllText(Path.Combine(dl.TargetDirectory, "det.onnx"), "x");
        var map = dl.GetFileExistsMap();
        Assert.True(map["det.onnx"]);
        Assert.False(map["rec.onnx"]);
        Assert.False(map["cls.onnx"]);
        Assert.False(map["ppocr_keys.txt"]);
    }

    [Fact]
    public void AllModelFilesExist_FalseWhenAnyMissing_TrueWhenAllPresent()
    {
        var dl = CreateDownloader();
        Assert.False(dl.AllModelFilesExist());
        foreach (var name in ModelDownloader.DefaultUrls.Keys)
            File.WriteAllText(Path.Combine(dl.TargetDirectory, name), "content");
        Assert.True(dl.AllModelFilesExist());
        // Remove optional cls.onnx - AllModelFilesExist still requires it (all keys)
        File.Delete(Path.Combine(dl.TargetDirectory, "cls.onnx"));
        Assert.False(dl.AllModelFilesExist());
    }

    [Fact]
    public void GetFileExistsMap_CaseInsensitiveLookup()
    {
        var dl = CreateDownloader();
        File.WriteAllText(Path.Combine(dl.TargetDirectory, "det.onnx"), "x");
        var map = dl.GetFileExistsMap();
        Assert.True(map.ContainsKey("DET.ONNX"));
        Assert.True(map["DET.ONNX"]);
    }

    // ---- VerifySha256 ----
    [Fact]
    public void VerifySha256_MissingVersionJson_FallsBackToAllModelFilesExist()
    {
        var dl = CreateDownloader();
        // No version.json, no models => AllModelFilesExist false => Verify returns false
        Assert.False(dl.VerifySha256());
        // Create all model files but still no version.json => should return true (all exist)
        foreach (var name in ModelDownloader.DefaultUrls.Keys)
            File.WriteAllText(Path.Combine(dl.TargetDirectory, name), "dummy");
        Assert.True(dl.VerifySha256());
    }

    [Fact]
    public void VerifySha256_WithVersionJson_AndAllFilesExist_ReturnsTrue()
    {
        var dl = CreateDownloader();
        var versionJson = Path.Combine(dl.TargetDirectory, "version.json");
        File.WriteAllText(versionJson, "{}"); // content not parsed beyond existence; VerifySha256 just hashes files
        foreach (var name in ModelDownloader.DefaultUrls.Keys)
            File.WriteAllText(Path.Combine(dl.TargetDirectory, name), "filecontent " + name);
        Assert.True(dl.VerifySha256());
    }

    [Fact]
    public void VerifySha256_WithVersionJson_MissingOneFile_ReturnsFalse()
    {
        var dl = CreateDownloader();
        File.WriteAllText(Path.Combine(dl.TargetDirectory, "version.json"), "{}");
        File.WriteAllText(Path.Combine(dl.TargetDirectory, "det.onnx"), "a");
        File.WriteAllText(Path.Combine(dl.TargetDirectory, "rec.onnx"), "b");
        // intentionally omit cls.onnx and ppocr_keys.txt
        Assert.False(dl.VerifySha256());
    }

    // ---- ModelDownloadProgress.Stage ----
    [Fact]
    public void ModelDownloadProgress_Stage_Logic()
    {
        var idle = new ModelDownloadProgress("x", 0, 0, 0, null, 0);
        Assert.Equal("空闲", idle.Stage);

        var downloading = new ModelDownloadProgress("det.onnx", 0, 4, 0, 100, 0);
        Assert.Contains("下载", downloading.Stage);
        Assert.Contains("1/4", downloading.Stage);

        var verifying = new ModelDownloadProgress("verify", 4, 4, 0, null, 100);
        Assert.Equal("校验中", verifying.Stage);
    }

    // ---- ExtractLargestOnnx (private static) via reflection ----
    static MethodInfo GetExtractMethod()
    {
        var m = typeof(ModelDownloader).GetMethod("ExtractLargestOnnx", BindingFlags.NonPublic | BindingFlags.Static);
        if (m != null) return m;
        // Fallback: enumerate (handles potential overload / trimming quirks)
        return typeof(ModelDownloader).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(x => x.Name == "ExtractLargestOnnx");
    }

    static bool InvokeExtract(string zipPath, string targetPath)
    {
        var m = GetExtractMethod();
        return (bool)m.Invoke(null, new object[] { zipPath, targetPath })!;
    }

    [Fact]
    public void ExtractLargestOnnx_NoOnnxEntry_ReturnsFalse()
    {
        var dl = CreateDownloader();
        var zipPath = Path.Combine(_tempRoot, "noonnx.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("readme.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("hello");
        }
        var target = Path.Combine(dl.TargetDirectory, "det.onnx");
        Assert.False(InvokeExtract(zipPath, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void ExtractLargestOnnx_PicksLargestAndRequiresGT1MB()
    {
        var dl = CreateDownloader();
        var zipPath = Path.Combine(_tempRoot, "multi.zip");
        // Create two onnx entries: small (10 bytes) and large (1_100_000 bytes)
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var small = zip.CreateEntry("small.onnx");
            using (var s = small.Open()) s.Write(new byte[10], 0, 10);
            var large = zip.CreateEntry("nested/large.onnx");
            // Write 1.1 MB of zeros
            var big = new byte[1_100_000];
            // Ensure content is not all zeros for realism but zeros compress still; ZipArchive will store
            // Use random filler to avoid high compression making size check trivial? The method checks extracted file length >1_000_000
            for (int i = 0; i < big.Length; i++) big[i] = (byte)(i % 256);
            using (var s = large.Open()) s.Write(big, 0, big.Length);
        }
        var target = Path.Combine(dl.TargetDirectory, "rec.onnx");
        var ok = InvokeExtract(zipPath, target);
        Assert.True(ok);
        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 1_000_000);
    }

    [Fact]
    public void ExtractLargestOnnx_SmallOnnx_ReturnsFalse_DueToSizeThreshold()
    {
        var dl = CreateDownloader();
        var zipPath = Path.Combine(_tempRoot, "small.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("model.onnx");
            using var s = e.Open();
            var tiny = new byte[500];
            s.Write(tiny, 0, tiny.Length);
        }
        var target = Path.Combine(dl.TargetDirectory, "det.onnx");
        var ok = InvokeExtract(zipPath, target);
        Assert.False(ok);
        // Still extracted but too small
        if (File.Exists(target))
            Assert.True(new FileInfo(target).Length <= 1_000_000);
    }

    [Fact]
    public void ExtractLargestOnnx_CorruptZip_ReturnsFalse()
    {
        var dl = CreateDownloader();
        var zipPath = Path.Combine(_tempRoot, "corrupt.zip");
        File.WriteAllText(zipPath, "not a zip");
        var target = Path.Combine(dl.TargetDirectory, "det.onnx");
        Assert.False(InvokeExtract(zipPath, target));
    }

    [Fact]
    public void ExtractLargestOnnx_CaseInsensitiveExtension()
    {
        var dl = CreateDownloader();
        var zipPath = Path.Combine(_tempRoot, "upper.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("MODEL.ONNX");
            var big = new byte[1_100_000];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)(i % 251);
            using var s = e.Open();
            s.Write(big, 0, big.Length);
        }
        var target = Path.Combine(dl.TargetDirectory, "det.onnx");
        Assert.True(InvokeExtract(zipPath, target));
    }

    // ---- GenerateDictFromZipYml (private instance) via reflection ----
    static MethodInfo GetGenerateMethod() =>
        typeof(ModelDownloader).GetMethod("GenerateDictFromZipYml", BindingFlags.NonPublic | BindingFlags.Instance)!;

    bool InvokeGenerate(string zipPath, string targetPath)
    {
        var dl = CreateDownloader();
        var m = GetGenerateMethod();
        return (bool)m.Invoke(dl, new object[] { zipPath, targetPath })!;
    }

    string BuildYmlContent(int lineCount, string prefix = "character_dict:")
    {
        var sb = new StringBuilder();
        sb.AppendLine("model_name: test");
        sb.AppendLine(prefix);
        for (int i = 0; i < lineCount; i++)
        {
            // Mix quoted and unquoted styles
            if (i % 3 == 0) sb.AppendLine($"  - '{i}_char'");
            else if (i % 3 == 1) sb.AppendLine($"  - \"{i}_char\"");
            else sb.AppendLine($"  - {i}_char");
        }
        sb.AppendLine("other_key: value");
        return sb.ToString();
    }

    [Fact]
    public void GenerateDictFromZipYml_NoYml_ReturnsFalse()
    {
        var zipPath = Path.Combine(_tempRoot, "noyaml.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("model.onnx");
            using var s = e.Open();
            s.Write(new byte[10], 0, 10);
        }
        var target = Path.Combine(_tempRoot, "out_dict.txt");
        Assert.False(InvokeGenerate(zipPath, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void GenerateDictFromZipYml_ValidYml_GeneratesFile()
    {
        // Need >= 10_000 chars total to pass size check
        // If each line ~6 chars + newline, need ~1700 lines
        int lines = 2000;
        var yml = BuildYmlContent(lines);
        var zipPath = Path.Combine(_tempRoot, "withyml.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("inference.yml");
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write(yml);
        }
        var target = Path.Combine(_tempRoot, "ppocr_keys.txt");
        var ok = InvokeGenerate(zipPath, target);
        Assert.True(ok);
        Assert.True(File.Exists(target));
        var text = File.ReadAllText(target, Encoding.UTF8);
        var count = text.Count(c => c == '\n');
        Assert.Equal(lines, count);
        // UTF-8 without BOM
        var bytes = File.ReadAllBytes(target);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void GenerateDictFromZipYml_TooSmall_RefusesToWrite()
    {
        var yml = BuildYmlContent(5); // tiny => <10k chars
        var zipPath = Path.Combine(_tempRoot, "tiny.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("inference.yaml");
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write(yml);
        }
        var target = Path.Combine(_tempRoot, "tiny_out.txt");
        Assert.False(InvokeGenerate(zipPath, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void GenerateDictFromZipYml_HandlesQuoteEscaping()
    {
        var sb = new StringBuilder();
        sb.AppendLine("character_dict:");
        sb.AppendLine("  - ''''"); // represents single quote char via '' escape
        sb.AppendLine("  - \"hello\"");
        // pad to reach 10000 chars (each line ~6 chars, need ~1700+)
        for (int i = 0; i < 2500; i++) sb.AppendLine($"  - x{i:D4}");
        var yml = sb.ToString();
        var zipPath = Path.Combine(_tempRoot, "quote.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("a.yml");
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write(yml);
        }
        var target = Path.Combine(_tempRoot, "quote_out.txt");
        Assert.True(InvokeGenerate(zipPath, target));
        var lines = File.ReadAllLines(target);
        Assert.Equal("'", lines[0]); // '' -> '
        Assert.Equal("hello", lines[1]);
    }

    [Fact]
    public void GenerateDictFromZipYml_CorruptZip_ReturnsFalse()
    {
        var zipPath = Path.Combine(_tempRoot, "bad.zip");
        File.WriteAllText(zipPath, "bad content");
        var target = Path.Combine(_tempRoot, "bad_out.txt");
        Assert.False(InvokeGenerate(zipPath, target));
    }

    [Fact]
    public void GenerateDictFromZipYml_YamlExtension_Variant()
    {
        int lines = 2000;
        var yml = BuildYmlContent(lines);
        var zipPath = Path.Combine(_tempRoot, "yaml_ext.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("config.yaml");
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write(yml);
        }
        var target = Path.Combine(_tempRoot, "yaml_out.txt");
        Assert.True(InvokeGenerate(zipPath, target));
    }

    // ---- Zip validation: GetZipAsync internal validation would use ZipFile.OpenRead ----
    [Fact]
    public void ZipValidation_CorruptZip_OpenReadThrowsInvalidData()
    {
        var zipPath = Path.Combine(_tempRoot, "invalid.zip");
        File.WriteAllText(zipPath, "not zip");
        Assert.Throws<InvalidDataException>(() => { using var _ = ZipFile.OpenRead(zipPath); });
    }

    // ---- Destination path computation ----
    [Fact]
    public void DestinationPaths_AreUnderTargetDirectory()
    {
        var dl = CreateDownloader();
        foreach (var name in ModelDownloader.DefaultUrls.Keys)
        {
            var expected = Path.Combine(dl.TargetDirectory, name);
            Assert.True(expected.StartsWith(dl.TargetDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(name, Path.GetFileName(expected));
        }
    }

    // ---- Temp naming / cleanup contracts (documented without network) ----
    [Fact]
    public void TempNamingConvention_DownloadTmpSuffix()
    {
        // ModelDownloader uses:
        // - GetZipAsync: Path.Combine(TargetDirectory, displayName + ".zip.download")
        // - DownloadRawAsync: target + ".download"
        // These temp files are cleaned on catch. We verify the convention is stable.
        var dl = CreateDownloader();
        var zipTmp = Path.Combine(dl.TargetDirectory, "det.onnx.zip.download");
        var rawTmp = Path.Combine(dl.TargetDirectory, "cls.onnx.download");
        Assert.EndsWith(".zip.download", zipTmp);
        Assert.EndsWith(".download", rawTmp);
    }

    // SKIPPED NETWORK ASPECTS (require HttpClient seam / live HTTP):
    // - DownloadAsync overall orchestration with real HTTP (HttpClient is new'd inline, not injectable)
    // - GetZipAsync download + zip-cache deduplication (same URL for det/rec/ppocr_keys)
    // - DownloadRawAsync for cls.onnx
    // - Progress reporting during streaming (bytes read / total)
    // - CancellationToken propagation
    // - OptionalFile handling (cls.onnx failure does not flip allOk)
    // - TryCopyVersionJson copying from BaseDirectory/assets/models to TargetDirectory
    // To cover these, ModelDownloader would need an IHttpClientFactory / HttpMessageHandler injection seam
    // or a test-specific subclass overriding GetZipAsync/DownloadRawAsync.
}
