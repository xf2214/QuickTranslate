using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QuickTranslate.Infrastructure.AppData;

namespace QuickTranslate.Infrastructure.Services;

public interface IModelDownloader
{
    string TargetDirectory { get; }
    bool AllModelFilesExist();
    IReadOnlyDictionary<string, bool> GetFileExistsMap();
    Task<bool> DownloadAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default);
    bool VerifySha256();
}

public record ModelDownloadProgress(
    string CurrentFile,
    int CurrentIndex,
    int TotalFiles,
    long BytesRead,
    long? TotalBytes,
    double OverallPercent)
{
    public string Stage => TotalFiles == 0 ? "空闲" :
        CurrentIndex < TotalFiles ? $"下载 {CurrentFile} ({CurrentIndex + 1}/{TotalFiles})" : "校验中";
}

/// <summary>
/// OCR 模型下载器。
/// det/rec 从 HoVDuc/ppocrv5-onnx 的 GitHub Release zip 下载并解压出 inference.onnx；
/// 字典 ppocr_keys.txt 由 rec zip 内的 inference.yml（PostProcess.character_dict）生成——
/// 它必须与 rec.onnx 输出类别数严格配套（v6 medium：18708 字符 + blank + 空格 = 18710 类），
/// 历史 Bug：曾固定下载 ppocr_keys_v1.txt（6623 行）与 v6 模型错配，CTC 解码全乱码。
/// cls.onnx 为可选件，下载失败仅告警不阻断。
/// </summary>
public class ModelDownloader : IModelDownloader
{
    public static readonly IReadOnlyDictionary<string, string> DefaultUrls = new Dictionary<string, string>
    {
        ["det.onnx"] = "https://github.com/HoVDuc/ppocrv5-onnx/releases/download/v1.1.0/PP-OCRv6_medium_det_onnx.zip",
        ["cls.onnx"] = "https://paddleocr.bj.bcebos.com/dygraph_v2.0/ch/ch_ppocr_mobile_v2.0_cls_infer.onnx",
        ["rec.onnx"] = "https://github.com/HoVDuc/ppocrv5-onnx/releases/download/v1.1.0/PP-OCRv6_medium_rec_onnx.zip",
        ["ppocr_keys.txt"] = "https://github.com/HoVDuc/ppocrv5-onnx/releases/download/v1.1.0/PP-OCRv6_medium_rec_onnx.zip"
    };

    // 文件名 → 下载后处理方式
    private enum SourceKind
    {
        ZipOnnx,      // 下载 zip → 取最大 .onnx
        ZipYmlDict,   // 下载 zip（与 rec 同源）→ 从 inference.yml 生成字典
        Raw           // 直接下载文件本体
    }

    private static readonly IReadOnlyDictionary<string, SourceKind> SourceKinds = new Dictionary<string, SourceKind>
    {
        ["det.onnx"] = SourceKind.ZipOnnx,
        ["rec.onnx"] = SourceKind.ZipOnnx,
        ["ppocr_keys.txt"] = SourceKind.ZipYmlDict,
        ["cls.onnx"] = SourceKind.Raw
    };

    private static readonly IReadOnlyList<string> OptionalFiles = new[] { "cls.onnx" };

    private readonly IAppDataProvider _appDataProvider;
    private readonly ILogger<ModelDownloader> _logger;

    public string TargetDirectory { get; }

    public ModelDownloader(IAppDataProvider appDataProvider, ILogger<ModelDownloader> logger)
    {
        _appDataProvider = appDataProvider;
        _logger = logger;
        var appDataModels = Path.Combine(_appDataProvider.GetAppDataDirectory(), "models");
        Directory.CreateDirectory(appDataModels);
        TargetDirectory = appDataModels;
    }

    public IReadOnlyDictionary<string, bool> GetFileExistsMap()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in DefaultUrls.Keys)
        {
            map[name] = File.Exists(Path.Combine(TargetDirectory, name));
        }
        return map;
    }

    public bool AllModelFilesExist() => GetFileExistsMap().All(kv => kv.Value);

    public async Task<bool> DownloadAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        // det 与 ppocr_keys 共用 rec 的 zip：实际网络下载按 URL 去重
        try
        {
            Directory.CreateDirectory(TargetDirectory);

            var pending = DefaultUrls.Keys.Where(n => !File.Exists(Path.Combine(TargetDirectory, n))).ToList();
            if (pending.Count == 0)
            {
                progress?.Report(new ModelDownloadProgress("verify", DefaultUrls.Count, DefaultUrls.Count, 0, null, 100.0));
                return VerifySha256();
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

            // url → 本地 zip 缓存路径（同 URL 只下载一次）
            var zipCache = new ConcurrentDictionary<string, string>();
            int idx = 0;
            bool allOk = true;

            foreach (var name in DefaultUrls.Keys)
            {
                if (File.Exists(Path.Combine(TargetDirectory, name)))
                {
                    idx++;
                    continue;
                }

                _logger.LogInformation("Downloading {File} from {Url}", name, DefaultUrls[name]);
                progress?.Report(new ModelDownloadProgress(name, idx, DefaultUrls.Count, 0, null, idx * 100.0 / DefaultUrls.Count));

                bool isOptional = OptionalFiles.Contains(name);
                bool ok;
                try
                {
                    switch (SourceKinds[name])
                    {
                        case SourceKind.Raw:
                            ok = await DownloadRawAsync(http, DefaultUrls[name], Path.Combine(TargetDirectory, name), progress, name, idx, ct).ConfigureAwait(false);
                            break;
                        case SourceKind.ZipOnnx:
                        {
                            var zip = await GetZipAsync(http, DefaultUrls[name], zipCache, progress, name, idx, ct).ConfigureAwait(false);
                            ok = zip != null && ExtractLargestOnnx(zip, Path.Combine(TargetDirectory, name));
                            break;
                        }
                        case SourceKind.ZipYmlDict:
                        {
                            var zip = await GetZipAsync(http, DefaultUrls[name], zipCache, progress, name, idx, ct).ConfigureAwait(false);
                            ok = zip != null && GenerateDictFromZipYml(zip, Path.Combine(TargetDirectory, name));
                            break;
                        }
                        default:
                            ok = false;
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Model download canceled");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download {File}", name);
                    ok = false;
                }

                if (!ok && !isOptional)
                {
                    allOk = false;
                }
                idx++;
            }

            // 顺带把 version.json 放到模型旁，供 ModelVersionVerifier 做完整性校验
            TryCopyVersionJson();

            progress?.Report(new ModelDownloadProgress("verify", DefaultUrls.Count, DefaultUrls.Count, 0, null, 100.0));
            return allOk && AllModelFilesExist();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model download failed");
            return false;
        }
    }

    private async Task<string?> GetZipAsync(
        HttpClient http, string url, ConcurrentDictionary<string, string> zipCache,
        IProgress<ModelDownloadProgress>? progress, string displayName, int idx, CancellationToken ct)
    {
        if (zipCache.TryGetValue(url, out var cached) && File.Exists(cached))
        {
            return cached;
        }

        var tmp = Path.Combine(TargetDirectory, displayName + ".zip.download");
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            await using var contentStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0)
                {
                    progress?.Report(new ModelDownloadProgress(displayName, idx, DefaultUrls.Count, read, total,
                        Math.Min(100.0, (idx + read * 1.0 / total.Value) * 100.0 / DefaultUrls.Count)));
                }
            }

            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        // 校验 zip 完整性
        try
        {
            using var _ = ZipFile.OpenRead(tmp);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Downloaded zip for {File} is corrupt", displayName);
            try { File.Delete(tmp); } catch { }
            return null;
        }

        zipCache[url] = tmp;
        return tmp;
    }

    private async Task<bool> DownloadRawAsync(
        HttpClient http, string url, string target,
        IProgress<ModelDownloadProgress>? progress, string displayName, int idx, CancellationToken ct)
    {
        var tmp = target + ".download";
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var contentStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            int n;
            while ((n = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target);
        _logger.LogInformation("{File} downloaded: {Bytes} bytes", displayName, new FileInfo(target).Length);
        return true;
    }

    private static bool ExtractLargestOnnx(string zipPath, string targetPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries
                .Where(e => e.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();
            if (entry == null) return false;

            entry.ExtractToFile(targetPath, overwrite: true);
            return new FileInfo(targetPath).Length > 1_000_000;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Regex YmlListItem = new(@"^\s*-\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex YmlTopKey = new(@"^[A-Za-z_]", RegexOptions.Compiled);

    /// <summary>从 rec zip 的 inference.yml 提取 character_dict 生成 ppocr_keys.txt（UTF-8 无 BOM，每行一字符）。</summary>
    private bool GenerateDictFromZipYml(string zipPath, string targetPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var ymlEntry = zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
            if (ymlEntry == null) return false;

            var sb = new StringBuilder();
            bool inDict = false;
            using var reader = new StreamReader(ymlEntry.Open(), Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (YmlTopKey.IsMatch(line))
                {
                    inDict = line.TrimStart().StartsWith("character_dict:", StringComparison.Ordinal);
                    continue;
                }

                if (!inDict) continue;
                var m = YmlListItem.Match(line);
                if (!m.Success) continue;

                var v = m.Groups[1].Value;
                if (v.Length >= 2 && v.StartsWith('\'') && v.EndsWith('\''))
                {
                    v = v.Substring(1, v.Length - 2).Replace("''", "'");
                }
                else if (v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"'))
                {
                    v = v.Substring(1, v.Length - 2);
                }
                sb.Append(v).Append('\n');
            }

            var text = sb.ToString();
            if (text.Length < 10_000)
            {
                _logger.LogWarning("Generated dictionary too small ({Len} chars); refuse to write", text.Length);
                return false;
            }

            File.WriteAllText(targetPath, text, new UTF8Encoding(false));
            _logger.LogInformation("Generated ppocr_keys.txt from inference.yml: {Lines} lines", text.Count(ch => ch == '\n'));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate dictionary from zip yml");
            return false;
        }
    }

    private void TryCopyVersionJson()
    {
        try
        {
            var dest = Path.Combine(TargetDirectory, "version.json");
            if (File.Exists(dest)) return;

            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "assets", "models", "version.json"),
                Path.Combine(AppContext.BaseDirectory, "models", "version.json"),
                @"E:\翻译\assets\models\version.json"
            };
            var src = candidates.FirstOrDefault(File.Exists);
            if (src != null)
            {
                File.Copy(src, dest, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not copy version.json next to downloaded models");
        }
    }

    public bool VerifySha256()
    {
        var versionJson = Path.Combine(TargetDirectory, "version.json");
        if (!File.Exists(versionJson))
        {
            _logger.LogWarning("version.json missing under {Dir}, skipping strict sha256 check", TargetDirectory);
            return AllModelFilesExist();
        }

        try
        {
            using var sha = SHA256.Create();
            var ok = true;
            foreach (var name in DefaultUrls.Keys)
            {
                var path = Path.Combine(TargetDirectory, name);
                if (!File.Exists(path)) { ok = false; continue; }
                using var s = File.OpenRead(path);
                var hash = Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
                _logger.LogInformation("SHA256 {File} = {Hash}", name, hash);
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SHA256 verification error");
            return AllModelFilesExist();
        }
    }
}
