using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using QuickTranslate.Infrastructure.AppData;

namespace QuickTranslate.Infrastructure.Services;

public class ModelVersionVerifier
{
    private readonly IAppDataProvider _appDataProvider;
    private readonly ILogger<ModelVersionVerifier> _logger;

    public ModelVersionVerifier(IAppDataProvider appDataProvider, ILogger<ModelVersionVerifier> logger)
    {
        _appDataProvider = appDataProvider;
        _logger = logger;
    }

    public ModelVersionInfo? LoadedVersion { get; private set; }
    public Dictionary<string, bool> Sha256Match { get; } = new();
    public bool AllSha256Matched { get; private set; } = true;

    public void Verify()
    {
        try
        {
            var versionJsonPath = FindVersionJsonPath();
            if (versionJsonPath == null)
            {
                _logger.LogWarning("[ModelVersion] version.json not found in any candidate directory");
                return;
            }

            var json = File.ReadAllText(versionJsonPath);
            var versionInfo = JsonSerializer.Deserialize<ModelVersionInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            if (versionInfo == null)
            {
                _logger.LogWarning("[ModelVersion] Failed to deserialize version.json");
                return;
            }

            LoadedVersion = versionInfo;

            var modelsDir = Path.GetDirectoryName(versionJsonPath)!;
            var detMatch = CheckSha256(versionInfo.Det, modelsDir);
            var recMatch = CheckSha256(versionInfo.Rec, modelsDir);
            var clsMatch = CheckSha256(versionInfo.Cls, modelsDir);

            Sha256Match["det"] = detMatch;
            Sha256Match["rec"] = recMatch;
            Sha256Match["cls"] = clsMatch;

            AllSha256Matched = detMatch && recMatch && clsMatch;

            var shaStatus = AllSha256Matched ? "all matched" : "mismatch/missing";
            _logger.LogInformation(
                "[ModelVersion] det={DetVer} rec={RecVer} cls={ClsVer} (sha256: {ShaStatus})",
                versionInfo.Det?.Version ?? "?",
                versionInfo.Rec?.Version ?? "?",
                versionInfo.Cls?.Version ?? "?",
                shaStatus);

            if (!AllSha256Matched)
            {
                _logger.LogWarning("[ModelVersion] SHA256 mismatch or model missing; fallback to MockOcrEngine");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ModelVersion] Verification failed; continue with MockOcrEngine fallback");
        }
    }

    private string? FindVersionJsonPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "models", "version.json"),
            Path.Combine(_appDataProvider.GetAppDataDirectory(), "models", "version.json"),
            Path.Combine(AppContext.BaseDirectory, "assets", "models", "version.json"),
            @"E:\翻译\assets\models\version.json"
        };

        foreach (var p in candidates)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private bool CheckSha256(ModelAssetInfo? asset, string modelsDir)
    {
        if (asset == null) return true;
        var expected = asset.Sha256 ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        var fileName = $"{asset.Name}.{asset.Format}";
        var filePath = Path.Combine(modelsDir, fileName);
        if (!File.Exists(filePath))
        {
            return asset.Optional;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(stream);
            var actual = Convert.ToHexString(hashBytes).ToLowerInvariant();
            var match = string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!match)
            {
                _logger.LogWarning("[ModelVersion] {Asset} sha256 mismatch: expected={Expected} actual={Actual}",
                    asset.Name, expected, actual);
            }
            return match;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ModelVersion] Failed to compute SHA256 for {Asset}", asset.Name);
            return asset.Optional;
        }
    }

    public string GetSummaryText()
    {
        if (LoadedVersion == null) return "version.json 未加载";
        var det = LoadedVersion.Det;
        var rec = LoadedVersion.Rec;
        var cls = LoadedVersion.Cls;
        return
            $"det: {det?.Name} v{det?.Version} (sha256 match: {Sha256Match.GetValueOrDefault("det", false)})\n" +
            $"rec: {rec?.Name} v{rec?.Version} (sha256 match: {Sha256Match.GetValueOrDefault("rec", false)})\n" +
            $"cls: {cls?.Name} v{cls?.Version} (sha256 match: {Sha256Match.GetValueOrDefault("cls", false)})\n" +
            $"{LoadedVersion.Note}";
    }
}

public class ModelVersionInfo
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("det")] public ModelAssetInfo? Det { get; set; }
    [JsonPropertyName("rec")] public ModelAssetInfo? Rec { get; set; }
    [JsonPropertyName("cls")] public ModelAssetInfo? Cls { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public class ModelAssetInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("optional")] public bool Optional { get; set; }
}
