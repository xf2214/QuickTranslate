using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.AppData;

namespace QuickTranslate.Infrastructure.Cache;

/// <summary>
/// ECDICT-lite 本地英汉词典实现。
/// 查找顺序（override chain）：
///   1. 用户自定义 overlay 词典（AppData 目录下 custom-dictionary.txt，优先级最高，缺词时用户自行追加）
///   2. 内置二进制 ecdict-lite.packed（当前随包资产为 50 万条）
/// 无任何内置兜底词表：packed 缺失时本地词典即为空，未命中词交给在线 Provider。
/// 仅当 targetLang 为中文（zh / zh-CN / zh-CN 变体）时才返回 true；反向 zh→en 不处理，走在线 Provider。
/// </summary>
public class EcdictLiteDictionary : ILocalDictionary
{
    private readonly IAppDataProvider _appData;
    private readonly ILogger<EcdictLiteDictionary> _logger;
    private readonly Lazy<DictionarySnapshot> _snapshot;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private const int MaxWordBytes = 1024;
    private const int MaxPhoneticBytes = 256;
    private const int MaxTranslationBytes = 64 * 1024;
    private const long MaxPackedBytes = 128L * 1024 * 1024;

    public int Count => _snapshot.Value.TotalCount;

    public EcdictLiteDictionary(
        IAppDataProvider appData,
        ILogger<EcdictLiteDictionary> logger)
    {
        _appData = appData;
        _logger = logger;
        _snapshot = new Lazy<DictionarySnapshot>(LoadSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool TryLookup(string word, string targetLang, out TranslationResult result)
    {
        result = default!;

        if (!IsChineseTarget(targetLang))
        {
            return false;
        }

        var key = NormalizeKey(word);
        if (key.Length == 0)
        {
            return false;
        }

        var snap = _snapshot.Value;

        // 1) 用户自定义 overlay（优先级最高，便于"缺词→补丁"场景）
        if (snap.Overlay.TryGetValue(key, out var overlayEntry))
        {
            result = BuildResult(word, targetLang, overlayEntry.Translation, overlayEntry.Phonetic,
                fromDictionary: true, fromOverlay: true);
            return true;
        }

        // 2) ECDICT-lite 主词典
        if (snap.Main.TryGetValue(key, out var mainEntry))
        {
            result = BuildResult(word, targetLang, mainEntry.Translation, mainEntry.Phonetic,
                fromDictionary: true, fromOverlay: false);
            return true;
        }

        return false;
    }

    public bool ContainsWord(string word)
    {
        var key = NormalizeKey(word);
        if (key.Length == 0) return false;
        var snapshot = _snapshot.Value;
        return snapshot.Overlay.ContainsKey(key) || snapshot.Main.ContainsKey(key);
    }

    // ============================================================
    // 内部实现
    // ============================================================

    private static string NormalizeKey(string? word) =>
        string.IsNullOrWhiteSpace(word) ? "" : word.Trim().ToLowerInvariant();

    private static bool IsChineseTarget(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return false;
        var l = lang.Trim().ToLowerInvariant();
        return l == "zh" || l.StartsWith("zh-", StringComparison.Ordinal) ||
               l.StartsWith("zh_", StringComparison.Ordinal) || l == "chinese" || l == "cn" || l == "zho";
    }

    private static TranslationResult BuildResult(
        string srcWord, string targetLang, string translation, string? phonetic,
        bool fromDictionary, bool fromOverlay)
    {
        // 统一格式：如果有音标则放在最前面，用 [音标] 包裹；之后是释义
        // （Popup 只显示 TargetText，不单独展示 Phonetic，避免改 UI；以后 UI 扩展可以把音标拆出来）
        var display = string.IsNullOrWhiteSpace(phonetic)
            ? translation
            : $"[{phonetic}]  {translation}";

        var normalizedKey = $"{NormalizeKey(srcWord)}||{(targetLang ?? string.Empty).Trim().ToLowerInvariant()}";

        return new TranslationResult(
            NormalizedKey: normalizedKey,
            SourceText: srcWord ?? string.Empty,
            TargetText: display,
            TargetLanguage: targetLang,
            FromCache: false,
            FromDictionary: fromDictionary,
            NeedsOnline: false);
    }

    private DictionarySnapshot LoadSnapshot()
    {
        var main = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var overlay = new Dictionary<string, Entry>(StringComparer.Ordinal);

        // A corrupt override must not suppress a healthy deployed dictionary. Each
        // candidate is built privately and published only after complete validation.
        foreach (var packedPath in ResolvePackedPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(packedPath)) continue;
            try
            {
                main = LoadPacked(packedPath);
                _logger.LogInformation("ECDICT-lite loaded: {Count} entries", main.Count);
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
            {
                _logger.LogWarning("Dictionary candidate skipped [ErrorCode=DICTIONARY_PACKED_INVALID] [ExceptionType={ExceptionType}]", ex.GetType().Name);
            }
        }
        if (main.Count == 0)
        {
            _logger.LogInformation("ECDICT-lite packed 文件未找到，本地词典为空（仅剩用户 overlay）");
        }

        // --- 用户 overlay：AppData/custom-dictionary.txt  ---
        var overlayCount = LoadOverlay(overlay);
        if (overlayCount > 0)
        {
            _logger.LogInformation("Custom dictionary overlay loaded: {Count} entries", overlayCount);
        }

        return new DictionarySnapshot(
            main.ToFrozenDictionary(StringComparer.Ordinal),
            overlay.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private IEnumerable<string> ResolvePackedPaths()
    {
        const string Fn = "ecdict-lite.packed";
        // 1. AppData 目录（用户 / 测试放这里 → 优先级最高，可覆盖随包版）
        yield return Path.Combine(_appData.GetAppDataDirectory(), Fn);

        // 2. 随包部署（发布后 /assets/dictionaries 或 /dictionaries 子目录）
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "assets", "dictionaries", Fn);
        yield return Path.Combine(baseDir, "dictionaries", Fn);

        // 3. 仓库源代码树（开发阶段 fallback）
        var repoRoot = TryFindRepoRoot();
        if (repoRoot != null)
        {
            yield return Path.Combine(repoRoot, "assets", "dictionaries", Fn);
        }
    }

    private static string? TryFindRepoRoot()
    {
        var cur = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (cur == null) break;
            if (File.Exists(Path.Combine(cur, "QuickTranslate.sln"))) return cur;
            cur = Path.GetDirectoryName(cur);
        }
        return null;
    }

    private static Dictionary<string, Entry> LoadPacked(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > MaxPackedBytes) throw new InvalidDataException("Packed file exceeds size limit");
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: false);

        // Keep both deployed v1 formats: 4-byte "1DCE" and 8-byte "ECDICT\0\0".
        var magic = reader.ReadUInt32();
        if (magic != 0x45434431 && (magic != 0x49444345 || reader.ReadUInt32() != 0x00005443))
            throw new InvalidDataException("Bad packed magic");
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported packed version");
        var count = reader.ReadInt32();
        // Every record has three lengths plus nonempty word/translation payloads.
        // Validate against the actual file before using an external count as capacity.
        if (count <= 0 || count > 5_000_000 || (long)count * 14 > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid packed entry count");

        var entries = new Dictionary<string, Entry>(count, StringComparer.Ordinal);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxTranslationBytes);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var word = ReadField(reader, buffer, MaxWordBytes, allowEmpty: false);
                var phonetic = ReadField(reader, buffer, MaxPhoneticBytes, allowEmpty: true);
                var translation = ReadField(reader, buffer, MaxTranslationBytes, allowEmpty: false);
                var key = NormalizeKey(word);
                if (key.Length == 0 || string.IsNullOrWhiteSpace(translation)) continue;
                entries.TryAdd(key, new Entry(translation, string.IsNullOrWhiteSpace(phonetic) ? null : phonetic));
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing packed data");
            return entries;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ReadField(BinaryReader reader, byte[] buffer, int maximumBytes, bool allowEmpty)
    {
        var length = reader.ReadInt32();
        if (length < (allowEmpty ? 0 : 1) || length > maximumBytes)
            throw new InvalidDataException("Invalid packed field length");
        if (length == 0) return string.Empty;
        var bytes = buffer.AsSpan(0, length);
        reader.BaseStream.ReadExactly(bytes);
        return StrictUtf8.GetString(bytes);
    }

    private int LoadOverlay(Dictionary<string, Entry> dest)
    {
        var path = Path.Combine(_appData.GetAppDataDirectory(), "custom-dictionary.txt");
        if (!File.Exists(path)) return 0;

        int added = 0;
        try
        {
            // 简单格式，每行：word\tphonetic(可空)\ttranslation
            // 或者 ：word,translation （两种都支持）
            using var reader = new StreamReader(path, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                string word, phonetic, translation;
                if (line.Contains('\t', StringComparison.Ordinal))
                {
                    var parts = line.Split('\t', 3, StringSplitOptions.None);
                    word = parts[0].Trim();
                    phonetic = parts.Length > 1 ? parts[1].Trim() : "";
                    translation = parts.Length > 2 ? parts[2].Trim() : "";
                }
                else if (line.Contains(',', StringComparison.Ordinal))
                {
                    var idx = line.IndexOf(',', StringComparison.Ordinal);
                    word = line.Substring(0, idx).Trim();
                    phonetic = "";
                    translation = line.Substring(idx + 1).Trim();
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation)) continue;
                var key = NormalizeKey(word);
                dest[key] = new Entry(translation, string.IsNullOrWhiteSpace(phonetic) ? null : phonetic);
                added++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 custom-dictionary.txt 失败，已忽略：{Path}", path);
        }

        return added;
    }

    // ============================================================
    // 内部嵌套类型
    // ============================================================

    private readonly record struct Entry(string Translation, string? Phonetic);

    private sealed class DictionarySnapshot
    {
        public FrozenDictionary<string, Entry> Main { get; }
        public FrozenDictionary<string, Entry> Overlay { get; }
        public int TotalCount => Main.Count + Overlay.Count;

        public DictionarySnapshot(
            FrozenDictionary<string, Entry> main,
            FrozenDictionary<string, Entry> overlay)
        {
            Main = main;
            Overlay = overlay;
        }
    }
}
