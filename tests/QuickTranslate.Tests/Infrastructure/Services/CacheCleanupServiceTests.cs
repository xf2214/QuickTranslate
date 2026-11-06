using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Cache;
using QuickTranslate.Infrastructure.Services;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure.Services;

public class CacheCleanupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _appDataDir;
    private readonly string _logDir;
    private readonly IAppDataProvider _mockProvider;
    private readonly ILogger<CacheCleanupService> _logger = NullLogger<CacheCleanupService>.Instance;

    public CacheCleanupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"qt_cleanup_test_{Guid.NewGuid():N}");
        _appDataDir = Path.Combine(_tempRoot, "AppData");
        _logDir = Path.Combine(_tempRoot, "Logs");
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(_logDir);

        _mockProvider = Substitute.For<IAppDataProvider>();
        _mockProvider.GetAppDataDirectory().Returns(_appDataDir);
        _mockProvider.GetLogDirectory().Returns(_logDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task GetStorageSummaryAsync_CalculatesCountsAndSizesAccurately()
    {
        // Arrange: Create dummy db, logs, and temp downloads
        var dbPath = Path.Combine(_appDataDir, "translate_cache_v1.db");
        File.WriteAllBytes(dbPath, new byte[1024]);

        var log1 = Path.Combine(_logDir, "QuickTranslate-20261104.log");
        File.WriteAllBytes(log1, new byte[2048]);

        var modelsDir = Path.Combine(_appDataDir, "models");
        Directory.CreateDirectory(modelsDir);
        var tempDownload = Path.Combine(modelsDir, "part.download");
        File.WriteAllBytes(tempDownload, new byte[512]);

        var mockL2 = Substitute.For<ITranslationL2Cache>();
        mockL2.GetCountAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(42L));

        var mockL1 = Substitute.For<ITranslationCache>();
        mockL1.Count.Returns(7);

        var service = new CacheCleanupService(_mockProvider, _logger, mockL2, mockL1);

        // Act
        var summary = await service.GetStorageSummaryAsync();

        // Assert
        Assert.Equal(7, summary.L1ItemCount);
        Assert.Equal(42, summary.L2RowCount);
        Assert.Equal(1024, summary.L2FileSizeBytes);
        Assert.Equal(1, summary.LogFileCount);
        Assert.Equal(2048, summary.LogFileSizeBytes);
        Assert.Equal(512, summary.TempFileSizeBytes);
        Assert.Equal(1024 + 2048 + 512, summary.TotalDiskBytes);
    }

    [Fact]
    public async Task CleanupAsync_DeletesLogsAndTempFiles_AndClearsCaches()
    {
        // Arrange
        var logFile = Path.Combine(_logDir, "test.log");
        File.WriteAllBytes(logFile, new byte[100]);

        var modelsDir = Path.Combine(_appDataDir, "models");
        Directory.CreateDirectory(modelsDir);
        var tempFile = Path.Combine(modelsDir, "incomplete.download");
        File.WriteAllBytes(tempFile, new byte[200]);

        var mockL2 = Substitute.For<ITranslationL2Cache>();
        var mockL1 = Substitute.For<ITranslationCache>();

        var service = new CacheCleanupService(_mockProvider, _logger, mockL2, mockL1);

        // Act
        var result = await service.CleanupAsync();

        // Assert
        mockL1.Received(1).Clear();
        await mockL2.Received(1).ClearAllAsync(Arg.Any<CancellationToken>());

        Assert.False(File.Exists(logFile));
        Assert.False(File.Exists(tempFile));
        Assert.Equal(1, result.LogsDeleted);
        Assert.Equal(1, result.TempFilesDeleted);
        Assert.True(result.L2Cleared);
        Assert.True(result.FreedBytes >= 300);
    }

    [Fact]
    public async Task SqliteTranslationCache_ClearAllAsync_EmptiesRowsAndResetsCount()
    {
        var dbPath = Path.Combine(_appDataDir, "test_cache.db");
        var cacheLogger = NullLogger<SqliteTranslationCache>.Instance;

        using var cache = new SqliteTranslationCache(dbPath, 100, cacheLogger);

        var dummyResult = new TranslationResult(
            NormalizedKey: "en|zh-CN|0|hello",
            SourceText: "hello",
            TargetText: "你好",
            TargetLanguage: "zh-CN",
            FromCache: false);

        await cache.AddAsync("en|zh-CN|0|hello", dummyResult);
        var countBefore = await cache.GetCountAsync();
        Assert.Equal(1, countBefore);

        await cache.ClearAllAsync();

        var countAfter = await cache.GetCountAsync();
        Assert.Equal(0, countAfter);

        var retrieved = await cache.TryGetAsync("en|zh-CN|0|hello");
        Assert.Null(retrieved);
    }
}
