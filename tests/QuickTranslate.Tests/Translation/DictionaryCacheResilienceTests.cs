using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Cache;
using QuickTranslate.Tests.Infrastructure;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public sealed class DictionaryCacheResilienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qt-dictionary-resilience", Guid.NewGuid().ToString("N"));
    public DictionaryCacheResilienceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }

    private sealed class AppData(string directory) : IAppDataProvider
    {
        public string GetAppDataDirectory() => directory;
        public string GetLogDirectory() => Path.Combine(directory, "Logs");
    }

    private EcdictLiteDictionary Dictionary() => new(new AppData(_directory), NullLogger<EcdictLiteDictionary>.Instance);
    private string PackedPath => Path.Combine(_directory, "ecdict-lite.packed");

    private void WritePacked(Action<BinaryWriter> records, int count = 1, int version = 1)
    {
        using var writer = new BinaryWriter(File.Create(PackedPath), Encoding.UTF8);
        writer.Write(new byte[] { 0x31, 0x44, 0x43, 0x45 });
        writer.Write(version);
        writer.Write(count);
        records(writer);
    }

    private static void Field(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void Entry(BinaryWriter writer, string word, string translation)
    {
        Field(writer, word);
        Field(writer, "");
        Field(writer, translation);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized-phonetic")]
    [InlineData("negative-length")]
    [InlineData("short-translation")]
    [InlineData("invalid-utf8")]
    public void CorruptPacked_DoesNotPublishEarlierValidEntries(string corruption)
    {
        WritePacked(writer =>
        {
            Entry(writer, "unique-before-corruption-qt", "must not survive");
            if (corruption == "truncated") return;
            if (corruption == "negative-length") { writer.Write(-1); return; }
            Field(writer, "unique-corrupt-record-qt");
            if (corruption == "oversized-phonetic") { writer.Write(257); writer.Write(new byte[257]); return; }
            Field(writer, "");
            if (corruption == "short-translation") { writer.Write(10); writer.Write((byte)'x'); return; }
            writer.Write(1); writer.Write((byte)0xff);
        }, count: 2);
        File.WriteAllText(Path.Combine(_directory, "custom-dictionary.txt"), "unique-overlay-qt,有效覆盖\n");

        var dictionary = Dictionary();
        Assert.False(dictionary.TryLookup("unique-before-corruption-qt", "zh", out _));
        Assert.False(dictionary.TryLookup("unique-corrupt-record-qt", "zh", out _));
        Assert.True(dictionary.TryLookup("unique-overlay-qt", "zh", out var overlay));
        Assert.Equal("有效覆盖", overlay.TargetText);
    }

    [Fact]
    public void UnknownPackedVersion_IsRejected()
    {
        WritePacked(writer => Entry(writer, "unique-future-format-qt", "unknown"), version: 2);
        Assert.False(Dictionary().TryLookup("unique-future-format-qt", "zh", out _));
    }

    [Fact]
    public void CorruptUserPacked_FallsBackToRepositoryAsset()
    {
        // The tracked asset is the real deployment fallback, not a generated stub.
        File.WriteAllBytes(PackedPath, new byte[] { 1, 2, 3, 4 });
        Assert.True(Dictionary().TryLookup("computer", "zh", out var result));
        Assert.Contains("计算机", result.TargetText);
    }

    [Fact]
    public void WhitespaceTranslation_IsNotAnOfflineHit()
    {
        WritePacked(writer => Entry(writer, "unique-empty-translation-qt", "   "));
        Assert.False(Dictionary().TryLookup("unique-empty-translation-qt", "zh", out _));
    }

    [Theory]
    [InlineData("zhonsense")]
    [InlineData("zho-anything")]
    public void InvalidChinesePrefix_DoesNotReturnChineseTranslation(string language)
    {
        WritePacked(writer => Entry(writer, "hello", "你好"));
        Assert.False(Dictionary().TryLookup("hello", language, out _));
    }

    [Fact]
    public void Membership_IsExactAndIncludesOverlay_WithoutTranslationAllocation()
    {
        WritePacked(writer => Entry(writer, "hello", "你好"));
        File.WriteAllText(Path.Combine(_directory, "custom-dictionary.txt"), "unique-overlay-qt,覆盖\n");
        var dictionary = Dictionary();
        Func<string, bool> contains = dictionary.ContainsWord;
        Assert.True(contains("  HELLO  "));
        Assert.True(contains("unique-overlay-qt"));
        Assert.False(contains("hellos"));
        Assert.False(contains("   "));
        for (var i = 0; i < 100; i++) contains("hello");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) Assert.True(contains("hello"));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class LegacyDictionary : ILocalDictionary
    {
        public Action? OnLookup { get; init; }
        public int Count => 1;
        public bool TryLookup(string word, string targetLang, out TranslationResult result)
        {
            OnLookup?.Invoke();
            result = new("legacy", word, "词典", targetLang, FromDictionary: true);
            return word == "bank";
        }
    }

    [Fact]
    public void Membership_DefaultImplementation_PreservesLegacyDictionaryCompatibility()
    {
        ILocalDictionary dictionary = new LegacyDictionary();
        Func<string, bool> contains = dictionary.ContainsWord;
        Assert.True(contains("bank"));
        Assert.False(contains("missing"));
    }

    private sealed class Provider : ITranslationProvider
    {
        public string Name => "local-test";
        public int Calls { get; private set; }
        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            await Task.Yield();
            yield return new("", true, "在线成功");
        }
    }

    private sealed class FailingCache : ITranslationL2Cache
    {
        public Exception? ReadFailure { get; init; }
        public Exception? WriteFailure { get; init; }
        public int Reads { get; private set; }
        public Task<TranslationResult?> TryGetAsync(string key, CancellationToken ct = default)
        {
            Reads++;
            if (ReadFailure != null) throw ReadFailure;
            return Task.FromResult<TranslationResult?>(null);
        }
        public Task AddAsync(string key, TranslationResult value, CancellationToken ct = default)
        {
            if (WriteFailure != null) throw WriteFailure;
            return Task.CompletedTask;
        }
        public Task<int> TrimAsync(int keep, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static DefaultTranslationRouter Router(Provider provider, ITranslationL2Cache l2, List<SpyLogEntry>? logs = null, ILocalDictionary? dictionary = null)
        => new(new MemoryLruTranslationCache(), dictionary ?? new LegacyDictionary(), provider,
            Options.Create(new AppSettings()), new SpyLogger<DefaultTranslationRouter>(logs ?? new()), l2);

    [Fact]
    public async Task DictionaryHit_BypassesPersistentCacheEntirely()
    {
        var provider = new Provider();
        var cache = new FailingCache { ReadFailure = new IOException("sensitive-text") };
        var result = await Router(provider, cache).TranslateWordAsync("bank", "zh");
        Assert.True(result.FromDictionary);
        Assert.Equal("词典", result.TargetText);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PersistentCacheFailure_DoesNotLoseOnlineSuccess_AndLogsNoSensitiveData(bool block, bool writing)
    {
        var logs = new List<SpyLogEntry>();
        var failure = new IOException("sensitive-text api-key-secret");
        var cache = new FailingCache { ReadFailure = writing ? null : failure, WriteFailure = writing ? failure : null };
        var router = Router(new Provider(), cache, logs);
        var result = block ? await router.TranslateBlockAsync("missing", "zh") : await router.TranslateWordAsync("missing", "zh");
        Assert.Equal("在线成功", result.TargetText);
        Assert.Contains(logs, log => log.Message.Contains("CACHE_L2_", StringComparison.Ordinal));
        Assert.All(logs, log =>
        {
            Assert.Null(log.Exception);
            Assert.DoesNotContain("sensitive-text", log.Message);
            Assert.DoesNotContain("api-key-secret", log.Message);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentCacheCancellation_Propagates(bool writing)
    {
        var failure = new OperationCanceledException();
        var cache = new FailingCache { ReadFailure = writing ? null : failure, WriteFailure = writing ? failure : null };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Router(new Provider(), cache).TranslateWordAsync("missing", "zh"));
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotReturnDictionarySuccess()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Router(new Provider(), new FailingCache()).TranslateWordAsync("bank", "zh", cts.Token));
    }

    [Fact]
    public async Task CancelledDuringSynchronousDictionaryLookup_DoesNotReturnSuccess()
    {
        using var cts = new CancellationTokenSource();
        var dictionary = new LegacyDictionary { OnLookup = cts.Cancel };
        var router = Router(new Provider(), new FailingCache(), dictionary: dictionary);
        var pending = router.TranslateWordAsync("bank", "zh", cts.Token);
        Assert.True(cts.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
