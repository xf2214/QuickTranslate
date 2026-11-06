namespace QuickTranslate.Core.Cache;

public record CacheStorageSummary(
    long L1ItemCount,
    long L2RowCount,
    long L2FileSizeBytes,
    int LogFileCount,
    long LogFileSizeBytes,
    long TempFileSizeBytes)
{
    public long TotalDiskBytes => L2FileSizeBytes + LogFileSizeBytes + TempFileSizeBytes;
}

public record CacheCleanupResult(
    long FreedBytes,
    int LogsDeleted,
    int TempFilesDeleted,
    bool L2Cleared);

public interface ICacheCleanupService
{
    Task<CacheStorageSummary> GetStorageSummaryAsync(CancellationToken ct = default);
    Task<CacheCleanupResult> CleanupAsync(CancellationToken ct = default);
}
