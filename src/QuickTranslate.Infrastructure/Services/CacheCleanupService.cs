using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Infrastructure.AppData;

namespace QuickTranslate.Infrastructure.Services;

public class CacheCleanupService : ICacheCleanupService
{
    private readonly IAppDataProvider _appDataProvider;
    private readonly ITranslationL2Cache? _l2Cache;
    private readonly ITranslationCache? _l1Cache;
    private readonly ILogger<CacheCleanupService> _logger;

    public CacheCleanupService(
        IAppDataProvider appDataProvider,
        ILogger<CacheCleanupService> logger,
        ITranslationL2Cache? l2Cache = null,
        ITranslationCache? l1Cache = null)
    {
        _appDataProvider = appDataProvider;
        _logger = logger;
        _l2Cache = l2Cache;
        _l1Cache = l1Cache;
    }

    public async Task<CacheStorageSummary> GetStorageSummaryAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            long l1Count = _l1Cache?.Count ?? 0;
            long l2Count = 0;
            if (_l2Cache != null)
            {
                try
                {
                    l2Count = await _l2Cache.GetCountAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get L2 cache count");
                }
            }

            var appDataDir = _appDataProvider.GetAppDataDirectory();
            long l2SizeBytes = 0;
            if (Directory.Exists(appDataDir))
            {
                var dbFiles = Directory.GetFiles(appDataDir, "translate_cache_v1.db*");
                foreach (var file in dbFiles)
                {
                    try
                    {
                        l2SizeBytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Failed to read file size: {File}", file);
                    }
                }
            }

            var logDir = _appDataProvider.GetLogDirectory();
            int logCount = 0;
            long logSizeBytes = 0;
            if (Directory.Exists(logDir))
            {
                var logFiles = Directory.GetFiles(logDir, "*.log");
                logCount = logFiles.Length;
                foreach (var file in logFiles)
                {
                    try
                    {
                        logSizeBytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Failed to read log file size: {File}", file);
                    }
                }
            }

            var selfLog = Path.Combine(Path.GetTempPath(), "QuickTranslate-serilog-selflog.txt");
            if (File.Exists(selfLog))
            {
                try
                {
                    logSizeBytes += new FileInfo(selfLog).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to read selflog file size: {File}", selfLog);
                }
            }

            long tempSizeBytes = 0;
            var modelsDir = Path.Combine(appDataDir, "models");
            if (Directory.Exists(modelsDir))
            {
                var tempDownloads = Directory.GetFiles(modelsDir, "*.download", SearchOption.AllDirectories);
                foreach (var file in tempDownloads)
                {
                    try
                    {
                        tempSizeBytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Failed to read temp download size: {File}", file);
                    }
                }
            }

            return new CacheStorageSummary(l1Count, l2Count, l2SizeBytes, logCount, logSizeBytes, tempSizeBytes);
        }, ct).ConfigureAwait(false);
    }

    public async Task<CacheCleanupResult> CleanupAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            long freedBytes = 0;

            // 1. 清理 L1 内存缓存
            try
            {
                _l1Cache?.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear L1 cache");
            }

            // 2. 清理 L2 磁盘缓存
            bool l2Cleared = false;
            long l2SizeBefore = 0;
            var appDataDir = _appDataProvider.GetAppDataDirectory();
            if (Directory.Exists(appDataDir))
            {
                foreach (var file in Directory.GetFiles(appDataDir, "translate_cache_v1.db*"))
                {
                    try { l2SizeBefore += new FileInfo(file).Length; } catch { }
                }
            }

            if (_l2Cache != null)
            {
                try
                {
                    await _l2Cache.ClearAllAsync(ct).ConfigureAwait(false);
                    l2Cleared = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear L2 cache");
                }
            }

            long l2SizeAfter = 0;
            if (Directory.Exists(appDataDir))
            {
                foreach (var file in Directory.GetFiles(appDataDir, "translate_cache_v1.db*"))
                {
                    try { l2SizeAfter += new FileInfo(file).Length; } catch { }
                }
            }

            if (l2SizeBefore > l2SizeAfter)
            {
                freedBytes += (l2SizeBefore - l2SizeAfter);
            }

            // 3. 清理日志文件
            int logsDeleted = 0;
            var logDir = _appDataProvider.GetLogDirectory();
            if (Directory.Exists(logDir))
            {
                foreach (var filePath in Directory.GetFiles(logDir, "*.log"))
                {
                    try
                    {
                        var info = new FileInfo(filePath);
                        var len = info.Length;
                        info.Delete();
                        freedBytes += len;
                        logsDeleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Skipped locked log file: {File}", filePath);
                    }
                }
            }

            var selfLog = Path.Combine(Path.GetTempPath(), "QuickTranslate-serilog-selflog.txt");
            if (File.Exists(selfLog))
            {
                try
                {
                    var len = new FileInfo(selfLog).Length;
                    File.Delete(selfLog);
                    freedBytes += len;
                    logsDeleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Skipped locked selflog file: {File}", selfLog);
                }
            }

            // 4. 清理残留的模型下载临时碎片 (*.download)
            int tempFilesDeleted = 0;
            var modelsDir = Path.Combine(appDataDir, "models");
            if (Directory.Exists(modelsDir))
            {
                foreach (var file in Directory.GetFiles(modelsDir, "*.download", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        var len = info.Length;
                        info.Delete();
                        freedBytes += len;
                        tempFilesDeleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Skipped locked temp download file: {File}", file);
                    }
                }
            }

            _logger.LogInformation(
                "Cache cleanup completed: freed {Bytes} bytes, deleted {Logs} logs, {Temp} temp files, L2 cleared={L2}",
                freedBytes, logsDeleted, tempFilesDeleted, l2Cleared);

            return new CacheCleanupResult(freedBytes, logsDeleted, tempFilesDeleted, l2Cleared);
        }, ct).ConfigureAwait(false);
    }
}
