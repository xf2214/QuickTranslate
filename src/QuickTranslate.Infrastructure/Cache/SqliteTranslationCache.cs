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
    private readonly long _softLimit;
    // Only accessed under _gate (or during construction). Updates do not add rows.
    private long _rowCount;
    private readonly ILogger<SqliteTranslationCache> _logger;

    // Microsoft.Data.Sqlite 不支持同一连接上的并发命令（重叠的 async 命令会抛
    // InvalidOperationException）。Word(Alt+1) 与 Block(Alt+2) 管线可能并行翻译，
    // 单操作都是亚毫秒级，用串行闸完全够用。
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteTranslationCache(string dbPath, int maxKeep, ILogger<SqliteTranslationCache> logger)
    {
        _dbPath = dbPath;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxKeep);
        _maxKeep = maxKeep;
        _softLimit = (long)maxKeep + Math.Min(256, Math.Max(1, maxKeep / 10));
        _logger = logger;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, Pooling = true, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
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
        _rowCount = CountRows();
        if (_rowCount > _maxKeep)
        {
            try { _rowCount -= TrimCore(_maxKeep); }
            catch (Exception ex) when (IsRecoverable(ex)) { LogFailure("CACHE_L2_TRIM", ex); }
        }
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
            CREATE INDEX IF NOT EXISTS ix_translation_cache_hits ON translation_cache(hits);
            CREATE INDEX IF NOT EXISTS ix_translation_cache_eviction
                ON translation_cache(hits DESC, last_hit_at DESC, id DESC);";
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

            TranslationResult? result;
            try { result = JsonSerializer.Deserialize<TranslationResult>(json, JsonOpts); }
            catch (JsonException ex)
            {
                LogFailure("CACHE_L2_INVALID", ex);
                result = null;
            }
            if (result != null && result.NormalizedKey == normalizedKey &&
                result.SourceText != null && !string.IsNullOrWhiteSpace(result.TargetText)) return result;

            // Invalid persisted data is a miss; do not let one poisoned entry break
            // every subsequent translation for this key. Never log JSON or key text.
            using var delete = _conn.CreateCommand();
            delete.CommandText = "DELETE FROM translation_cache WHERE key_hash=$hash";
            delete.Parameters.AddWithValue("$hash", hash);
            _rowCount -= await delete.ExecuteNonQueryAsync(ct);
            _logger.LogWarning("Invalid cache entry removed [ErrorCode=CACHE_L2_INVALID]");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Successful writes retain at most maxKeep + min(256, max(1, maxKeep / 10))
    /// rows. Crossing that limit trims to maxKeep. An update does not advance the
    /// count. New insertion and maintenance are atomic: failed trimming skips the
    /// new cache write, rather than permitting unbounded growth during failures.
    /// </summary>
    public async Task AddAsync(string normalizedKey, TranslationResult result, CancellationToken ct = default)
    {
        if (result == null) return;
        var hash = TranslationCacheKey.Sha256Hex(normalizedKey);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(result, JsonOpts);

        await _gate.WaitAsync(ct);
        try
        {
            using var transaction = _conn.BeginTransaction();
            using var command = _conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO translation_cache(key_hash,key_payload,src_lang,dst_lang,mode,result_json,created_at,last_hit_at,hits)
                VALUES($hash,$payload,$src,$dst,$mode,$json,$t,$t,1)
                ON CONFLICT(key_hash) DO NOTHING";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$payload", normalizedKey);
            var parts = normalizedKey.Split('|', 4);
            command.Parameters.AddWithValue("$src", parts.Length >= 1 ? parts[0] : "");
            command.Parameters.AddWithValue("$dst", parts.Length >= 2 ? parts[1] : "");
            command.Parameters.AddWithValue("$mode", parts.Length >= 3 && int.TryParse(parts[2], out var mode) ? mode : 0);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$t", now);
            var inserted = await command.ExecuteNonQueryAsync(ct);
            if (inserted == 0)
            {
                command.CommandText = @"UPDATE translation_cache SET result_json=$json,
                    last_hit_at=$t,hits=hits+1 WHERE key_hash=$hash";
                await command.ExecuteNonQueryAsync(ct);
            }

            var deleted = 0;
            if (_rowCount + inserted > _softLimit)
            {
                ct.ThrowIfCancellationRequested();
                try { deleted = TrimCore(_maxKeep, transaction); }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    ct.ThrowIfCancellationRequested();
                    LogFailure("CACHE_L2_TRIM", ex);
                    return; // transaction disposal rolls back the new insertion too
                }
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            _rowCount += inserted - deleted;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> TrimAsync(int keep, CancellationToken ct = default)
    {
        if (keep <= 0) keep = _maxKeep;
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var deleted = TrimCore(keep);
            _rowCount -= deleted;
            return deleted;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ct.ThrowIfCancellationRequested();
            LogFailure("CACHE_L2_TRIM", ex);
            return 0;
        }
        finally { _gate.Release(); }
    }

    private long CountRows()
    {
        using var command = _conn.CreateCommand();
        command.CommandText = "SELECT count(*) FROM translation_cache";
        return (long)command.ExecuteScalar()!;
    }

    private int TrimCore(int keep, SqliteTransaction? transaction = null)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"DELETE FROM translation_cache WHERE id NOT IN (
            SELECT id FROM translation_cache ORDER BY hits DESC,last_hit_at DESC,id DESC LIMIT $keep)";
        command.Parameters.AddWithValue("$keep", keep);
        return command.ExecuteNonQuery();
    }

    private void LogFailure(string code, Exception exception)
        => _logger.LogWarning("Optional cache maintenance skipped [ErrorCode={ErrorCode}] [ExceptionType={ExceptionType}]",
            code, exception.GetType().Name);

    private static bool IsRecoverable(Exception exception) => exception is
        SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;

    public void Dispose()
    {
        _conn.Dispose();
        _gate.Dispose();
    }
}
