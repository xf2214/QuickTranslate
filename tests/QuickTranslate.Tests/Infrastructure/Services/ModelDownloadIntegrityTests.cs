using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Services;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure.Services;

public sealed class ModelDownloadIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-integrity-" + Guid.NewGuid().ToString("N"));
    private ModelDownloader Create() => new(new TestAppData(_root), NullLogger<ModelDownloader>.Instance);

    private static void WriteValidModels(ModelDownloader downloader, bool includeClassifier = false)
    {
        var manifest = new Dictionary<string, object> { ["schemaVersion"] = 1 };
        foreach (var (key, name, format, optional) in new[]
        {
            ("det", "det", "onnx", false), ("rec", "rec", "onnx", false),
            ("keys", "ppocr_keys", "txt", false), ("cls", "cls", "onnx", true)
        })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("fixture-" + name);
            if (!optional || includeClassifier)
                File.WriteAllBytes(Path.Combine(downloader.TargetDirectory, name + "." + format), bytes);
            manifest[key] = new
            {
                name, format, optional, version = "fixture-v1",
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
        }
        File.WriteAllText(Path.Combine(downloader.TargetDirectory, "version.json"), JsonSerializer.Serialize(manifest));
    }

    [Fact]
    public void RequiredModelsWithMatchingHashes_SucceedWithoutOptionalClassifier()
    {
        var downloader = Create();
        WriteValidModels(downloader);
        Assert.True(downloader.AllModelFilesExist());
        Assert.True(downloader.VerifySha256());
    }

    [Theory]
    [InlineData("det.onnx")]
    [InlineData("rec.onnx")]
    [InlineData("ppocr_keys.txt")]
    [InlineData("cls.onnx")]
    public void ChangedFile_IsRejected(string filename)
    {
        var downloader = Create();
        WriteValidModels(downloader, includeClassifier: true);
        File.WriteAllText(Path.Combine(downloader.TargetDirectory, filename), "corrupted");
        Assert.False(downloader.VerifySha256());
    }

    [Fact]
    public async Task DownloadWithExistingCorruptFile_DoesNotReportSuccess()
    {
        var downloader = Create();
        WriteValidModels(downloader, includeClassifier: true);
        File.WriteAllText(Path.Combine(downloader.TargetDirectory, "rec.onnx"), "corrupted");

        Assert.False(await downloader.DownloadAsync());
    }

    [Fact]
    public async Task DownloadWithExistingVerifiedFiles_SucceedsWithoutNetwork()
    {
        var downloader = Create();
        WriteValidModels(downloader, includeClassifier: true);

        Assert.True(await downloader.DownloadAsync());
    }

    [Theory]
    [InlineData("det.onnx")]
    [InlineData("rec.onnx")]
    [InlineData("ppocr_keys.txt")]
    public void MissingRequiredFile_IsRejected(string filename)
    {
        var downloader = Create();
        WriteValidModels(downloader, includeClassifier: true);
        File.Delete(Path.Combine(downloader.TargetDirectory, filename));
        Assert.False(downloader.AllModelFilesExist());
        Assert.False(downloader.VerifySha256());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":99}")]
    public void InvalidManifest_CannotClaimVerification(string manifest)
    {
        var downloader = Create();
        WriteValidModels(downloader, includeClassifier: true);
        File.WriteAllText(Path.Combine(downloader.TargetDirectory, "version.json"), manifest);
        Assert.False(downloader.VerifySha256());
    }

    [Fact]
    public void MissingManifest_CannotClaimVerification()
    {
        var downloader = Create();
        WriteValidModels(downloader, includeClassifier: true);
        File.Delete(Path.Combine(downloader.TargetDirectory, "version.json"));
        Assert.False(downloader.VerifySha256());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class TestAppData(string root) : IAppDataProvider
    {
        public string GetAppDataDirectory() => root;
        public string GetLogDirectory() => Path.Combine(root, "logs");
    }
}
