using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Cache;

public class SqliteTranslationCache : ITranslationL2Cache, IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _conn;
    private readonly int _maxKeep;
    private readonly ILogger<SqliteTranslationCache> _logger;

    public SqliteTranslationCache(string dbPath, int maxKeep, ILogger<SqliteTranslationCache> logger)
    {
        _dbPath = dbPath;
        _maxKeep = maxKeep;
        _logger = logger;

        var connectionString = $"Data Source={_dbPath};Pooling=True;Mode=ReadWriteCreate;Cache=Shared;";
        _conn = new SqliteConnection(connectionString);
        _conn.Open();

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
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM translation_cache WHERE key_hash = $hash LIMIT 1";
        cmd.Parameters.AddWithValue("$hash", hash);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        if (json == null) return null;

        using (var upd = _conn.CreateCommand())
        {
            upd.CommandText = "UPDATE translation_cache SET last_hit_at = $t, hits = hits + 1 WHERE key_hash = $hash";
            upd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            upd.Parameters.AddWithValue("$hash", hash);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return JsonSerializer.Deserialize<TranslationResult>(json, new JsonSerializerOptions { IncludeFields = true })
            ?? throw new InvalidOperationException("Bad cache JSON");
    }

    public async Task AddAsync(string normalizedKey, TranslationResult result, CancellationToken ct = default)
    {
        if (result == null) return;

        var hash = TranslationCacheKey.Sha256Hex(normalizedKey);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true });

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

    public async Task<int> TrimAsync(int keep, CancellationToken ct = default)
    {
        if (keep <= 0) keep = _maxKeep;

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

    public void Dispose()
    {
        _conn.Dispose();
    }
}
