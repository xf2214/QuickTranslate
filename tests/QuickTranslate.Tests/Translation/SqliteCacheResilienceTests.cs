using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using QuickTranslate.Tests.Infrastructure;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public sealed class SqliteCacheResilienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qt-sqlite-resilience", Guid.NewGuid().ToString("N"));
    public SqliteCacheResilienceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
    private string PathToDb => Path.Combine(_directory, "cache.db");
    private SqliteTranslationCache Cache(int keep = 10) => new(PathToDb, keep, NullLogger<SqliteTranslationCache>.Instance);
    private static TranslationResult Result(string key) => new(key, key, "译文", "zh");

    private object? Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathToDb }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"NormalizedKey\":\"wrong\",\"TargetText\":\"译文\"}")]
    public async Task InvalidCachedResult_IsRemovedAndBecomesMiss(string invalidJson)
    {
        using var cache = Cache();
        await cache.AddAsync("key", Result("key"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathToDb }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE translation_cache SET result_json=$json";
            command.Parameters.AddWithValue("$json", invalidJson); command.ExecuteNonQuery();
        }
        Assert.Null(await cache.TryGetAsync("key"));
        Assert.Equal(0L, Sql("SELECT count(*) FROM translation_cache"));
    }

    [Fact]
    public async Task Capacity_IsBoundedAndUpdatesDoNotTriggerUnnecessaryEviction()
    {
        using var cache = Cache(10);
        for (var i = 0; i < 11; i++) await cache.AddAsync($"key{i}", Result($"key{i}"));
        Assert.Equal(11L, Sql("SELECT count(*) FROM translation_cache"));
        for (var i = 0; i < 30; i++) await cache.AddAsync("key0", Result("key0"));
        Assert.Equal(11L, Sql("SELECT count(*) FROM translation_cache"));
        await cache.AddAsync("key11", Result("key11"));
        Assert.Equal(10L, Sql("SELECT count(*) FROM translation_cache"));
        Assert.NotNull(await cache.TryGetAsync("key0"));
        for (var i = 12; i < 100; i++)
        {
            await cache.AddAsync($"key{i}", Result($"key{i}"));
            Assert.InRange((long)Sql("SELECT count(*) FROM translation_cache")!, 10, 11);
        }
    }

    [Fact]
    public async Task ReopeningOversizedCache_TrimsImmediately_WithStableTieBreak()
    {
        using (var cache = Cache(100))
            for (var i = 0; i < 20; i++) await cache.AddAsync($"key{i}", Result($"key{i}"));
        Sql("UPDATE translation_cache SET hits=1,last_hit_at=1");
        using var reopened = Cache(10);
        Assert.Equal(10L, Sql("SELECT count(*) FROM translation_cache"));
        Assert.Null(await reopened.TryGetAsync("key0"));
        Assert.NotNull(await reopened.TryGetAsync("key19"));
    }

    [Fact]
    public async Task TrimFailure_SkipsNewCacheWrite_AndRetriesOnNextWrite()
    {
        var logs = new List<SpyLogEntry>();
        using var cache = new SqliteTranslationCache(PathToDb, 1, new SpyLogger<SqliteTranslationCache>(logs));
        await cache.AddAsync("a", Result("a"));
        await cache.AddAsync("b", Result("b"));
        Sql("CREATE TRIGGER fail_trim BEFORE DELETE ON translation_cache BEGIN SELECT RAISE(FAIL,'sensitive-cache-content'); END;");
        await cache.AddAsync("c", Result("c"));
        Assert.Null(await cache.TryGetAsync("c"));
        Assert.Equal(2L, Sql("SELECT count(*) FROM translation_cache"));
        Assert.Contains(logs, log => log.Message.Contains("CACHE_L2_TRIM", StringComparison.Ordinal));
        Assert.All(logs, log => { Assert.Null(log.Exception); Assert.DoesNotContain("sensitive-cache-content", log.Message); });
        Sql("DROP TRIGGER fail_trim");
        await cache.AddAsync("d", Result("d"));
        Assert.Equal(1L, Sql("SELECT count(*) FROM translation_cache"));
    }
}
