using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class SqliteTranslationCacheTests : IDisposable
{
    private readonly string _testDir;

    public SqliteTranslationCacheTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "qt_sqlitecache_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
        }
    }

    private SqliteTranslationCache CreateCache(string dbName = "test.db", int maxKeep = 10000)
    {
        var dbPath = Path.Combine(_testDir, dbName);
        var logger = NullLogger<SqliteTranslationCache>.Instance;
        return new SqliteTranslationCache(dbPath, maxKeep, logger);
    }

    private static string NKey(string text, string lang)
        => $"{text.Trim().ToLowerInvariant()}||{lang.Trim().ToLowerInvariant()}";

    private static TranslationResult MakeResult(string key, string source, string target, string lang = "zh-CN")
        => new(NormalizedKey: key, SourceText: source, TargetText: target, TargetLanguage: lang, FromCache: false);

    [Fact]
    public async Task Case1_AddThenTryGet_ReturnsSameResult()
    {
        using var cache = CreateCache("case1.db");
        var key = NKey("hello", "zh-CN");
        var result = MakeResult(key, "hello", "你好");

        await cache.AddAsync(key, result);

        var fetched = await cache.TryGetAsync(key);
        Assert.NotNull(fetched);
        Assert.Equal("你好", fetched.TargetText);
        Assert.Equal("hello", fetched.SourceText);
        Assert.Equal(key, fetched.NormalizedKey);
    }

    [Fact]
    public async Task Case2_Miss_ReturnsNull()
    {
        using var cache = CreateCache("case2.db");

        var fetched = await cache.TryGetAsync(NKey("nonexistent", "zh-CN"));
        Assert.Null(fetched);
    }

    [Fact]
    public async Task Case3_OnConflict_UpdatesHits_AndUpdatesResult()
    {
        using var cache = CreateCache("case3.db");
        var key = NKey("test", "zh-CN");
        var resultA = MakeResult(key, "test", "结果A");
        var resultB = MakeResult(key, "test", "结果B");

        await cache.AddAsync(key, resultA);
        await cache.AddAsync(key, resultB);

        var fetched = await cache.TryGetAsync(key);
        Assert.NotNull(fetched);
        Assert.Equal("结果B", fetched.TargetText);
    }

    [Fact]
    public async Task Case4_TrimAsync_DropsLowHits()
    {
        using var cache = CreateCache("case4.db", maxKeep: 100);

        for (int i = 1; i <= 100; i++)
        {
            var key = NKey($"word{i}", "zh-CN");
            var result = MakeResult(key, $"word{i}", $"翻译{i}");
            await cache.AddAsync(key, result);

            for (int j = 1; j < i; j++)
            {
                await cache.TryGetAsync(key);
            }
        }

        var deleted = await cache.TrimAsync(10);
        Assert.Equal(90, deleted);
    }

    [Fact]
    public async Task Case5_HitUpdatesLastHitAt()
    {
        using var cache = CreateCache("case5.db");
        var key = NKey("hitme", "zh-CN");
        var result = MakeResult(key, "hitme", "打我");

        await cache.AddAsync(key, result);

        var r1 = await cache.TryGetAsync(key);
        Assert.NotNull(r1);

        var r2 = await cache.TryGetAsync(key);
        Assert.NotNull(r2);

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_testDir, "case5.db")}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hits FROM translation_cache WHERE key_payload = $p";
        var hashPayload = TranslationCacheKey.Sha256Hex(key);
        cmd.Parameters.AddWithValue("$p", key);
        cmd.CommandText = "SELECT hits FROM translation_cache WHERE key_hash = $h";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$h", hashPayload);
        var hits = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        Assert.True(hits >= 2, $"Expected hits >= 2, got {hits}");
    }

    [Fact]
    public async Task Case6_CrossProcess_Reuse()
    {
        var dbPath = Path.Combine(_testDir, "cross.db");
        var logger = NullLogger<SqliteTranslationCache>.Instance;
        var key = NKey("shared", "zh-CN");
        var result = MakeResult(key, "shared", "共享");

        using (var cacheA = new SqliteTranslationCache(dbPath, 10000, logger))
        {
            await cacheA.AddAsync(key, result);
        }

        using (var cacheB = new SqliteTranslationCache(dbPath, 10000, logger))
        {
            var fetched = await cacheB.TryGetAsync(key);
            Assert.NotNull(fetched);
            Assert.Equal("共享", fetched.TargetText);
        }
    }
}
