using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Cache;

public class SqliteTranslationCache : ITranslationL2Cache, IDisposable
{
    // JsonSerializer 的元数据缓存绑定在 options 实例上：每次调用 new 一个会丢失缓存、
    // 每次序列化/反序列化都重建类型元数据。翻译热路径上必须复用同一实例。
    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    private readonly string _dbPath;
    private readonly SqliteConnection _conn;
    private readonly int _maxKeep;
    private readonly ILogger<SqliteTranslationCache> _logger;

    // Microsoft.Data.Sqlite 不支持同一连接上的并发命令（重叠的 async 命令会抛
    // InvalidOperationException）。Word(Alt+1) 与 Block(Alt+2) 管线可能并行翻译，
    // 单操作都是亚毫秒级，用串行闸完全够用。
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteTranslationCache(string dbPath, int maxKeep, ILogger<SqliteTranslationCache> logger)
    {
        _dbPath = dbPath;
        _maxKeep = maxKeep;
        _logger = logger;

        var connectionString = $"Data Source={_dbPath};Pooling=True;Mode=ReadWriteCreate;Cache=Shared;";
        _conn = new SqliteConnection(connectionString);
        _conn.Open();

        // 性能关键：默认 rollback journal + FULL fsync 下单次写实测 ~6ms，
        // 而 L2 读写都在“识别→翻译”关键路径上。WAL + synchronous=NORMAL
        // 把写降到亚毫秒级，且对单写者多读者的翻译缓存场景完全安全。
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteScalar();
        }
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        EnsureTablesCreated();
    }

    private void EnsureTablesCreated()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS translation_cache (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                key_hash TEXT NOT NULL UNIQUE,
                key_payload TEXT,
                src_lang TEXT,
                dst_lang TEXT,
                mode INTEGER,
                result_json TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                last_hit_at INTEGER NOT NULL,
                hits INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_translation_cache_last_hit ON translation_cache(last_hit_at);
            CREATE INDEX IF NOT EXISTS ix_translation_cache_hits ON translation_cache(hits);";
        cmd.ExecuteNonQuery();
    }

    public async Task<TranslationResult?> TryGetAsync(string normalizedKey, CancellationToken ct = default)
    {
        var hash = TranslationCacheKey.Sha256Hex(normalizedKey);
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            // 单语句完成“命中计数 + 取结果”：旧实现 SELECT + UPDATE 两次往返，
            // 命中的读也要付一次写盘；UPDATE...RETURNING 未命中时返回空（null）。
            cmd.CommandText = @"UPDATE translation_cache SET last_hit_at = $t, hits = hits + 1
            WHERE key_hash = $hash
            RETURNING result_json";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$hash", hash);
            var json = (string?)await cmd.ExecuteScalarAsync(ct);
            if (json == null) return null;

            return JsonSerializer.Deserialize<TranslationResult>(json, JsonOpts)
                ?? throw new InvalidOperationException("Bad cache JSON");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(string normalizedKey, TranslationResult result, CancellationToken ct = default)
    {
        if (result == null) return;

        var hash = TranslationCacheKey.Sha256Hex(normalizedKey);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(result, JsonOpts);

        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO translation_cache(key_hash,key_payload,src_lang,dst_lang,mode,result_json,created_at,last_hit_at,hits)
          VALUES($hash,$payload,$src,$dst,$mode,$json,$t,$t,1)
          ON CONFLICT(key_hash) DO UPDATE SET
            result_json = excluded.result_json,
            last_hit_at = excluded.last_hit_at,
            hits = hits + 1;";
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$payload", normalizedKey);

            var parts = normalizedKey.Split('|', 4);
            cmd.Parameters.AddWithValue("$src", parts.Length >= 1 ? parts[0] : "");
            cmd.Parameters.AddWithValue("$dst", parts.Length >= 2 ? parts[1] : "");
            cmd.Parameters.AddWithValue("$mode", parts.Length >= 3 && int.TryParse(parts[2], out var m) ? m : 0);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$t", now);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> TrimAsync(int keep, CancellationToken ct = default)
    {
        if (keep <= 0) keep = _maxKeep;

        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM translation_cache WHERE id NOT IN (
            SELECT id FROM translation_cache ORDER BY hits DESC, last_hit_at DESC LIMIT $keep)";
            cmd.Parameters.AddWithValue("$keep", keep);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);

            if (deleted > 0)
            {
                _logger.LogInformation("Trimmed L2 cache: removed {Count} rows, keeping top {Keep}", deleted, keep);
            }

            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _gate.Dispose();
    }
}
