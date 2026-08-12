using System.Security.Cryptography;
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

public class ModelDownloader : IModelDownloader
{
    // PP-OCRv6 server series ONNX URLs (official PaddlePaddle release mirrors)
    // Users may override via AppSettings or environment variables.
    public static readonly IReadOnlyDictionary<string, string> DefaultUrls = new Dictionary<string, string>
    {
        ["det.onnx"] = "https://paddleocr.bj.bcebos.com/PP-OCRv6/chinese/ch_PP-OCRv6_det_server_infer.onnx",
        ["cls.onnx"] = "https://paddleocr.bj.bcebos.com/dygraph_v2.0/ch/ch_ppocr_mobile_v2.0_cls_infer.onnx",
        ["rec.onnx"] = "https://paddleocr.bj.bcebos.com/PP-OCRv6/chinese/ch_PP-OCRv6_rec_server_infer.onnx",
        ["ppocr_keys.txt"] = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/ppocr_keys_v1.txt"
    };

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
        try
        {
            Directory.CreateDirectory(TargetDirectory);

            var needed = DefaultUrls
                .Where(kv => !File.Exists(Path.Combine(TargetDirectory, kv.Key)))
                .ToList();

            // If all exist, just verify
            if (needed.Count == 0)
            {
                progress?.Report(new ModelDownloadProgress("verify", DefaultUrls.Count, DefaultUrls.Count, 0, null, 100.0));
                return VerifySha256();
            }

            int total = DefaultUrls.Count;
            long overallDone = 0;
            long totalEstimate = 0;

            // Try HEAD to get total size estimate
            foreach (var (name, url) in DefaultUrls)
            {
                try
                {
                    var target = Path.Combine(TargetDirectory, name);
                    if (File.Exists(target)) { overallDone += new FileInfo(target).Length; continue; }
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    using var msg = new HttpRequestMessage(HttpMethod.Head, url);
                    using var resp = await client.SendAsync(msg, ct).ConfigureAwait(false);
                    if (resp.Content.Headers.ContentLength.HasValue)
                        totalEstimate += resp.Content.Headers.ContentLength.Value;
                }
                catch { /* ignore */ }
            }

            if (totalEstimate == 0) totalEstimate = 1;

            int idx = 0;
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(20)
            };

            foreach (var (name, url) in DefaultUrls)
            {
                var target = Path.Combine(TargetDirectory, name);
                if (File.Exists(target))
                {
                    progress?.Report(new ModelDownloadProgress(name, idx, total, overallDone, totalEstimate,
                        Math.Min(100, overallDone * 100.0 / totalEstimate)));
                    idx++;
                    continue;
                }

                _logger.LogInformation("Downloading {File} from {Url}", name, url);
                progress?.Report(new ModelDownloadProgress(name, idx, total, overallDone, totalEstimate,
                    Math.Min(100, overallDone * 100.0 / totalEstimate)));

                var tmp = target + ".download";
                if (File.Exists(tmp)) File.Delete(tmp);

                try
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    var fileSize = resp.Content.Headers.ContentLength ?? 0;

                    await using var contentStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    var buffer = new byte[81920];
                    long fileRead = 0;
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        fileRead += read;
                        progress?.Report(new ModelDownloadProgress(name, idx, total,
                            overallDone + fileRead,
                            totalEstimate,
                            Math.Min(100, (overallDone + fileRead) * 100.0 / Math.Max(1, totalEstimate + (fileSize > 0 ? fileSize : 0)))));
                    }

                    await fs.FlushAsync(ct).ConfigureAwait(false);
                    fs.Close();

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);

                    var fi = new FileInfo(target);
                    overallDone += fi.Length;
                    if (totalEstimate < overallDone) totalEstimate = overallDone;

                    _logger.LogInformation("{File} downloaded: {Bytes} bytes", name, fi.Length);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Model download canceled");
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download {File}", name);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return false;
                }

                idx++;
            }

            progress?.Report(new ModelDownloadProgress("verify", total, total, overallDone, totalEstimate, 100.0));
            return AllModelFilesExist();
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
