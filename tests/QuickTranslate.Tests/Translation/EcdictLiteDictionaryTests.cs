using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Cache;
using QuickTranslate.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace QuickTranslate.Tests.Translation;

[Trait("Category", "Dictionary")]
public class EcdictLiteDictionaryTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tempRoot;

    public EcdictLiteDictionaryTests(ITestOutputHelper output)
    {
        _out = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), "qtec", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* ignore */ }
    }

    // ============================================================
    //  1. 无 packed 文件 → 自动回退到 stub 59 词
    // ============================================================
    [Fact]
    public void NoPackedFile_FallsBackToStubEntries()
    {
        var appData = MakeAppData(out _);
        var dict = new EcdictLiteDictionary(appData, NullLogger<EcdictLiteDictionary>.Instance);

        // stub 内有 computer → 电脑
        var ok = dict.TryLookup("computer", "zh-CN", out var result);

        Assert.True(ok, "stub computer should hit");
        Assert.Contains("电脑", result.TargetText);
        Assert.True(result.FromDictionary);
        Assert.False(result.NeedsOnline);
        _out.WriteLine("computer -> {0}", result.TargetText);
    }

    [Fact]
    public void NonChineseTarget_ReturnsFalse_EvenIfWordExists()
    {
        var appData = MakeAppData(out _);
        var dict = new EcdictLiteDictionary(appData, NullLogger<EcdictLiteDictionary>.Instance);

        var ok = dict.TryLookup("computer", "en", out _);

        Assert.False(ok, "en target should not be served by local ECDICT (zh only)");
    }

    [Fact]
    public void Lookup_CaseAndWhitespaceInsensitive()
    {
        var appData = MakeAppData(out _);
        var dict = new EcdictLiteDictionary(appData, NullLogger<EcdictLiteDictionary>.Instance);

        var ok1 = dict.TryLookup("  COMPUTER  ", "zh", out var r1);
        var ok2 = dict.TryLookup("Computer", "zh-cn", out var r2);

        Assert.True(ok1); Assert.True(ok2);
        Assert.Equal(r1.TargetText, r2.TargetText);
    }

    // ============================================================
    //  2. 用户自定义 overlay 能覆盖主词典 / stub
    // ============================================================
    [Fact]
    public void CustomOverlay_HigherPriority_And_AddsMissing()
    {
        var appData = MakeAppData(out var appDataDir);

        // 1) 写 overlay：保留 computer 原 stub；新增 stayover（stub 里没有）
        File.WriteAllText(
            Path.Combine(appDataDir, "custom-dictionary.txt"),
            "# 我的自定义补丁词典，支持 Tab 或逗号分隔\n" +
            "computer\t\t计算机；n. 计算机（自定义）\n" +       // 用空 phonetic 的 tab 格式，覆盖 stub "电脑"
            "stayover,（机场）中转住宿；中途停留\n" +           // 逗号格式，新增
            "hello\t həˈləʊ \t你好呀！\n"
        );

        var dict = new EcdictLiteDictionary(appData, NullLogger<EcdictLiteDictionary>.Instance);

        Assert.True(dict.TryLookup("computer", "zh", out var r1));
        Assert.Contains("计算机", r1.TargetText);            // 覆盖成功
        Assert.DoesNotContain("电脑", r1.TargetText);
        _out.WriteLine("computer (overlay) -> {0}", r1.TargetText);

        Assert.True(dict.TryLookup("stayover", "zh-CN", out var r2));
        Assert.Contains("中转住宿", r2.TargetText);
        _out.WriteLine("stayover (overlay new) -> {0}", r2.TargetText);

        Assert.True(dict.TryLookup("HELLO", "zh", out var r3));
        Assert.Contains("həˈləʊ", r3.TargetText);              // 音标能正确包含
        Assert.Contains("你好呀", r3.TargetText);
    }

    // ============================================================
    //  3. 用内存生成的 mini-packed 验证主词典加载路径
    //     避免依赖真实下载的大文件（测试是离线的）
    // ============================================================
    [Fact]
    public void PackedFile_LoadedAndLookupsWork_PhoneticIncluded()
    {
        var appData = MakeAppData(out _);
        // 把测试 packed 放在仓库风格的 assets/dictionaries 目录下（ResolvePackedPath 会查找 repo root）
        // 但为了测试隔离，我们直接把 packed 放到 AppData 目录（ResolvePackedPath 的最后候选）。
        var packedPath = Path.Combine(appData.GetAppDataDirectory(), "ecdict-lite.packed");
        WriteMiniPacked(packedPath, new (string W, string? P, string T)[]
        {
            ("algorithm", "ˈælɡərɪðəm", "n. 算法；计算程序"),
            ("function", "ˈfʌŋkʃən", "n. 功能；函数；职责\nv. 运行；起作用"),
            ("perceive", "pəˈsiːv", "v. 认为；察觉；理解；感知"),
            ("你好", null, "测试中文作为 key 不会崩"), // 非英文词 OCR 不会命中，但要保证 TryLookup 不炸
        });

        var logEntries = new List<SpyLogEntry>();
        var logger = new SpyLogger<EcdictLiteDictionary>(logEntries);
        var dict = new EcdictLiteDictionary(appData, logger);

        // 输出任何 Warnings / Errors（packed 加载失败会记为 Warning）
        foreach (var e in logEntries.Where(x => x.Level >= LogLevel.Warning))
        {
            _out.WriteLine("[{0}] {1} {2}", e.Level, e.Message, e.Exception != null ? ("\n  EX: " + e.Exception.ToString()) : "");
        }

        Assert.True(dict.TryLookup("algorithm", "zh", out var r1));
        Assert.Contains("ˈælɡərɪðəm", r1.TargetText);
        Assert.Contains("算法", r1.TargetText);
        _out.WriteLine("algorithm -> {0}", r1.TargetText);

        // function 的 stub 不存在（59 词里没有 function），说明来自 packed
        Assert.True(dict.TryLookup("Function", "zh-CN", out var r2));
        Assert.Contains("函数", r2.TargetText);

        // 未命中词条
        Assert.False(dict.TryLookup("nonexistent-word-xyz", "zh", out _));

        // 验证：packed 后 stub 依然可用（双重 fallback）
        Assert.True(dict.TryLookup("mouse", "zh", out var r3));
        Assert.Contains("鼠标", r3.TargetText);
    }

    // ============================================================
    //  4. packed magic / version / count 异常时回退到 stub（不 throw，保证应用可用）
    // ============================================================
    [Fact]
    public void CorruptedPacked_GracefulFallback_ToStub()
    {
        var appData = MakeAppData(out _);
        var packedPath = Path.Combine(appData.GetAppDataDirectory(), "ecdict-lite.packed");
        File.WriteAllBytes(packedPath, new byte[] { 0xBA, 0xAD, 0xF0, 0x0D });

        var dict = new EcdictLiteDictionary(appData, NullLogger<EcdictLiteDictionary>.Instance);

        // stub 应该依然工作
        Assert.True(dict.TryLookup("translate", "zh", out var r));
        Assert.Contains("翻译", r.TargetText);
    }

    [Fact]
    public void Count_Reflects_MainPlusOverlayPlusStub()
    {
        var appData = MakeAppData(out var appDataDir);
        var packedPath = Path.Combine(appDataDir, "ecdict-lite.packed");
        WriteMiniPacked(packedPath, new[]
        {
            ( "alpha", (string?)null, "阿尔法" ),
            ( "beta",  null, "贝塔" ),
            ( "gamma", null, "伽马" ),
        });
        File.WriteAllText(Path.Combine(appDataDir, "custom-dictionary.txt"),
            "delta,德尔塔\n" +
            "epsilon,艾普西隆\n");

        var dict = new EcdictLiteDictionary(appData, NullLogger<EcdictLiteDictionary>.Instance);
        // packed 3 + overlay 2 + stub（至少 50），所以总数应 >= 55
        // （不把 stub 精确值 59 写死在这里：避免未来 stub 增删导致维护成本）
        Assert.True(dict.Count >= 3 + 2 + 50, $"Count={dict.Count} too small (expected >= 55)");
        // 确保 packed 和 overlay 的每个词都能查到（它们不会被重复扣减）
        Assert.True(dict.TryLookup("alpha", "zh", out _));
        Assert.True(dict.TryLookup("beta", "zh", out _));
        Assert.True(dict.TryLookup("delta", "zh", out _));
        Assert.True(dict.TryLookup("epsilon", "zh", out _));
        _out.WriteLine("Total Count = {0}", dict.Count);
    }

    // ============================================================
    // helpers
    // ============================================================
    private IAppDataProvider MakeAppData(out string dir)
    {
        var d = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        dir = d;
        return new TestAppData(d);
    }

    private sealed class TestAppData : IAppDataProvider
    {
        private readonly string _dir;
        public TestAppData(string dir) => _dir = dir;
        public string GetAppDataDirectory() => _dir;
        public string GetLogDirectory() => Path.Combine(_dir, "Logs");
    }

    /// <summary> 把一批词写成与 download-ecdict-lite.ps1 相同规范的 packed 二进制。 </summary>
    private static void WriteMiniPacked(string path, IEnumerable<(string W, string? P, string T)> entries)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
        var magic = new byte[] { (byte)'E', (byte)'C', (byte)'D', (byte)'I', (byte)'C', (byte)'T', 0, 0 };
        bw.Write(magic);
        bw.Write((int)1);
        // 先数条目
        var list = entries.ToList();
        bw.Write(list.Count);
        var utf8 = Encoding.UTF8;
        foreach (var (w, p, t) in list)
        {
            var wb = utf8.GetBytes(w);
            var pb = string.IsNullOrEmpty(p) ? Array.Empty<byte>() : utf8.GetBytes(p);
            var tb = utf8.GetBytes(t);
            bw.Write(wb.Length); bw.Write(wb);
            bw.Write(pb.Length); if (pb.Length > 0) bw.Write(pb);
            bw.Write(tb.Length); bw.Write(tb);
        }
    }

    [Fact]
    public void Smoke_Real500KPacked_IfPresent_LoadsAndLooksUpCommonWords()
    {
        // 仅在开发者机器上仓库根的 assets/dictionaries/ecdict-lite.packed 存在时运行；
        // CI 环境没有该文件 -> Skip（不失败）。
        string repoRoot = TryFindRepoRoot();
        string realPacked = repoRoot == null ? "" : Path.Combine(repoRoot, "assets", "dictionaries", "ecdict-lite.packed");
        if (string.IsNullOrEmpty(realPacked) || !File.Exists(realPacked))
        {
            _out.WriteLine("(skip) real 500K packed not found at: {0}", realPacked);
            return;
        }

        var appData = MakeAppData(out var appDataDir);
        // 放到 AppData 下，EcdictLiteDictionary.ResolvePackedPath 会命中（见 ResolvePackedPath candidate 3）
        File.Copy(realPacked, Path.Combine(appDataDir, "ecdict-lite.packed"));

        var dict = new EcdictLiteDictionary(appData, NullLogger<EcdictLiteDictionary>.Instance);
        _out.WriteLine("Loaded Count = {0}", dict.Count);

        // 至少要 > packed 500K（stub + overlay 空 ≈ 500,000）
        Assert.True(dict.Count >= 490_000, $"Count={dict.Count} too small (expected >= 490K)");

        // 典型高频词查询（都是 ECDICT 100% 有翻译的；断言片段取自真实 ECDICT 500K packed）
        (string Word, string MustContainFragmentZh)[] probes = new[]
        {
            ("hello",      "喂"),          // [hә'lәu]  interj. 喂, 嘿
            ("algorithm",  "算法"),
            ("computer",   "计算机"),
            ("translate",  "翻译"),
            ("language",   "语言"),
            ("beauty",     "美"),          // ['bju:ti]  n. 美, 美人
            ("run",        "跑"),
            ("think",      "想"),          // θiŋk]  v. 想, 认为, 考虑
            ("book",       "书"),
            ("peace",      "和平"),
        };
        foreach (var (w, frag) in probes)
        {
            Assert.True(dict.TryLookup(w, "zh-CN", out var r), $"missed word: {w}");
            Assert.Contains(frag, r.TargetText);
            Assert.True(r.FromDictionary, w + " not marked as FromDictionary");
            _out.WriteLine("  {0,-12} -> {1}", w, r.TargetText.Length > 60 ? r.TargetText.Substring(0, 60) + "..." : r.TargetText);
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
}
