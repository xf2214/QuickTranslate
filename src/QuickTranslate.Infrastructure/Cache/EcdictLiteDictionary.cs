using System.Collections.Frozen;
using System.Text;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.AppData;

namespace QuickTranslate.Infrastructure.Cache;

/// <summary>
/// ECDICT-lite 本地英汉词典实现。
/// 查找顺序（fallback chain）：
///   1. 内置压缩二进制 ecdict-lite.packed（高频 3~5 万词，按词频精选）
///   2. 用户自定义 overlay 词典（AppData 目录下 custom-dictionary.txt，优先级最高，缺词时用户自行追加）
///   3. 兜底 59 词 StubMiniDictionary（保证极端情况下无 packed 也能用）
/// 仅当 targetLang 为中文（zh / zh-CN / zh-CN 变体）时才返回 true；反向 zh→en 不处理，走在线 Provider。
/// </summary>
public class EcdictLiteDictionary : ILocalDictionary
{
    private readonly IAppDataProvider _appData;
    private readonly ILogger<EcdictLiteDictionary> _logger;
    private readonly Lazy<DictionarySnapshot> _snapshot;

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

        // 3) 兜底 stub
        if (StubMiniDictionarySource.Entries.TryGetValue(key, out var stubTrans))
        {
            result = BuildResult(word, targetLang, stubTrans, phonetic: null,
                fromDictionary: true, fromOverlay: false);
            return true;
        }

        return false;
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
        return l.StartsWith("zh", StringComparison.Ordinal) || l == "chinese" || l == "cn" || l == "zho";
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

        // --- 主词典：按"嵌入资源 → 程序基目录/assets/dictionaries → AppData"顺序查找第一个存在的 packed 文件 ---
        var packedPath = ResolvePackedPath();
        int mainCount = 0;
        if (packedPath != null && File.Exists(packedPath))
        {
            try
            {
                mainCount = LoadPacked(packedPath, main);
                _logger.LogInformation("ECDICT-lite loaded: {Count} entries from {Path}", mainCount, packedPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ECDICT-lite packed 损坏或不可读，跳过：{Path}", packedPath);
            }
        }
        else
        {
            _logger.LogInformation("ECDICT-lite packed 文件未找到，仅使用 stub 59 词兜底");
        }

        // --- 用户 overlay：AppData/custom-dictionary.txt  ---
        var overlayCount = LoadOverlay(overlay);
        if (overlayCount > 0)
        {
            _logger.LogInformation("Custom dictionary overlay loaded: {Count} entries", overlayCount);
        }

        return new DictionarySnapshot(
            main.ToFrozenDictionary(StringComparer.Ordinal),
            overlay.ToFrozenDictionary(StringComparer.Ordinal),
            StubMiniDictionarySource.Entries.Count);
    }

    private string? ResolvePackedPath()
    {
        const string Fn = "ecdict-lite.packed";
        var candidates = new List<string>();

        // 1. AppData 目录（用户 / 测试放这里 → 优先级最高，可覆盖随包版）
        candidates.Add(Path.Combine(_appData.GetAppDataDirectory(), Fn));

        // 2. 随包部署（发布后 /assets/dictionaries 或 /dictionaries 子目录）
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "assets", "dictionaries", Fn));
        candidates.Add(Path.Combine(baseDir, "dictionaries", Fn));

        // 3. 仓库源代码树（开发阶段 fallback）
        var repoRoot = TryFindRepoRoot();
        if (repoRoot != null)
        {
            candidates.Add(Path.Combine(repoRoot, "assets", "dictionaries", Fn));
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return null;
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

    private static int LoadPacked(string path, Dictionary<string, Entry> dest)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        // 读取头 4 字节；有两种合法格式：
        //   Format A (8-byte magic, 测试辅助 WriteMiniPacked) : "ECDICT\0\0"
        //       bytes on disk: [0x45,0x43,0x44,0x49, 0x43,0x54,0x00,0x00]
        //   Format B (4-byte magic, download-ecdict-lite.ps1 v1) : LE uint32 0x45434431
        //       bytes on disk: [0x31,0x44,0x43,0x45]   (= ASCII "1DCE" as LE)
        var head4 = br.ReadBytes(4);
        if (head4.Length < 4) throw new InvalidDataException("Packed file too small");

        bool isFormatB = head4[0] == 0x31 && head4[1] == 0x44 &&
                         head4[2] == 0x43 && head4[3] == 0x45;  // "1DCE" LE

        if (!isFormatB)
        {
            // Format A：继续读剩下 4 字节，合计 8 字节比较
            var tail4 = br.ReadBytes(4);
            var expected = new byte[] { (byte)'E', (byte)'C', (byte)'D', (byte)'I', (byte)'C', (byte)'T', 0, 0 };
            if (!head4.Concat(tail4).SequenceEqual(expected))
            {
                throw new InvalidDataException("Bad packed magic (not an ECDICT packed file)");
            }
        }

        var version = br.ReadInt32();
        if (version < 1)
        {
            throw new InvalidDataException($"Unsupported packed version: {version}");
        }

        var count = br.ReadInt32();
        if (count <= 0 || count > 5_000_000)
        {
            throw new InvalidDataException($"Invalid count in packed: {count}");
        }

        var utf8 = Encoding.UTF8;
        int loaded = 0;
        for (int i = 0; i < count; i++)
        {
            int wLen = br.ReadInt32();
            if (wLen <= 0 || wLen > 1024) break;
            var wBytes = br.ReadBytes(wLen);

            int pLen = br.ReadInt32();
            byte[] pBytes = pLen > 0 ? br.ReadBytes(pLen) : Array.Empty<byte>();
            if (pLen < 0 || pLen > 256) break;

            int tLen = br.ReadInt32();
            if (tLen <= 0 || tLen > 64 * 1024) break;
            var tBytes = br.ReadBytes(tLen);

            var key = NormalizeKey(utf8.GetString(wBytes));
            if (key.Length == 0) continue;

            var translation = utf8.GetString(tBytes);
            var phonetic = pBytes.Length > 0 ? utf8.GetString(pBytes) : null;

            // 同名重复：保留第一个（通常是更常用的，也避免重复 Key 抛异常）
            if (!dest.ContainsKey(key))
            {
                dest[key] = new Entry(translation, phonetic);
                loaded++;
            }
        }

        return loaded;
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
        public int StubCount { get; }
        public int TotalCount => Main.Count + Overlay.Count + StubCount;

        public DictionarySnapshot(
            FrozenDictionary<string, Entry> main,
            FrozenDictionary<string, Entry> overlay,
            int stubCount)
        {
            Main = main;
            Overlay = overlay;
            StubCount = stubCount;
        }
    }
}

/// <summary>
/// 59 词兜底 stub，集中在独立的 source 类，避免和 EcdictLiteDictionary 耦合。
/// 内容与旧 StubMiniDictionary 完全一致，保证行为不回归。
/// </summary>
file static class StubMiniDictionarySource
{
    public static readonly IReadOnlyDictionary<string, string> Entries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["quick"] = "快速的",
        ["translate"] = "翻译",
        ["hello"] = "你好",
        ["world"] = "世界",
        ["focus"] = "焦点",
        ["book"] = "书",
        ["computer"] = "电脑",
        ["language"] = "语言",
        ["window"] = "窗户",
        ["mouse"] = "鼠标",
        ["keyboard"] = "键盘",
        ["screen"] = "屏幕",
        ["memory"] = "记忆",
        ["cache"] = "缓存",
        ["result"] = "结果",
        ["source"] = "来源",
        ["target"] = "目标",
        ["button"] = "按钮",
        ["click"] = "点击",
        ["open"] = "打开",
        ["close"] = "关闭",
        ["copy"] = "复制",
        ["paste"] = "粘贴",
        ["cut"] = "剪切",
        ["save"] = "保存",
        ["delete"] = "删除",
        ["search"] = "搜索",
        ["find"] = "找到",
        ["replace"] = "替换",
        ["file"] = "文件",
        ["folder"] = "文件夹",
        ["document"] = "文档",
        ["application"] = "应用程序",
        ["system"] = "系统",
        ["network"] = "网络",
        ["server"] = "服务器",
        ["client"] = "客户端",
        ["database"] = "数据库",
        ["image"] = "图片",
        ["video"] = "视频",
        ["audio"] = "音频",
        ["text"] = "文本",
        ["number"] = "数字",
        ["word"] = "单词",
        ["sentence"] = "句子",
        ["paragraph"] = "段落",
        ["page"] = "页面",
        ["line"] = "线条",
        ["point"] = "点",
        ["area"] = "区域"
    };
}
