using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class RouterCacheIsolationTests
{
    private sealed class Dictionary : ILocalDictionary
    {
        public int Count => 1;

        public bool TryLookup(string word, string targetLang, out TranslationResult result)
        {
            result = new TranslationResult("dictionary", word, "词典结果", targetLang, FromDictionary: true);
            return word == "bank";
        }
    }

    private sealed class Provider : ITranslationProvider
    {
        public Action? OnNameRead { get; set; }
        public string Name
        {
            get { OnNameRead?.Invoke(); return "TestProvider"; }
        }
        public int Calls { get; private set; }
        public Action? OnTranslate { get; set; }

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            OnTranslate?.Invoke();
            var text = $"online-{++Calls}:{request.Mode}:{request.Quality}:{request.Text}";
            await Task.Yield();
            yield return new TranslationChunk("", true, text);
        }
    }

    private sealed class DeferredCacheMiss : ITranslationL2Cache
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TranslationResult?> TryGetAsync(string normalizedKey, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(ct);
            return null;
        }

        public Task AddAsync(string normalizedKey, TranslationResult result, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> TrimAsync(int keep, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static DefaultTranslationRouter Router(
        Provider provider, AppSettings? settings = null, ITranslationCache? l1 = null, ITranslationL2Cache? l2 = null)
        => new(l1 ?? new MemoryLruTranslationCache(100), new Dictionary(), provider,
            Options.Create(settings ?? new AppSettings()), NullLogger<DefaultTranslationRouter>.Instance, l2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsChangedDuringL2Lookup_DoNotCacheNewProviderResultUnderOldIdentity(bool word)
    {
        var settings = new AppSettings { CustomLlmModel = "old-model" };
        var l2 = new DeferredCacheMiss();
        var router = Router(new Provider(), settings, l2: l2);
        Task<TranslationResult> Translate() => word
            ? router.TranslateWordAsync("hello", "zh-CN")
            : router.TranslateBlockAsync("hello", "zh-CN");

        var pending = Translate();
        await l2.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.CustomLlmModel = "new-model";
        l2.Continue.SetResult();
        var changed = await pending;
        settings.CustomLlmModel = "old-model";
        var old = await Translate();

        Assert.False(old.FromCache);
        Assert.NotEqual(changed.TargetText, old.TargetText);
    }

    [Fact]
    public async Task SettingsChangedDuringProviderCall_DoNotCacheUnderPreviousIdentity()
    {
        var settings = new AppSettings { CustomLlmModel = "old-model" };
        var provider = new Provider { OnTranslate = () => settings.CustomLlmModel = "new-model" };
        var router = Router(provider, settings);
        await router.TranslateWordAsync("hello", "zh-CN");
        settings.CustomLlmModel = "old-model";
        provider.OnTranslate = null;

        var old = await router.TranslateWordAsync("hello", "zh-CN");
        Assert.False(old.FromCache);
    }

    [Fact]
    public async Task SettingsChangedAndRestoredDuringProviderCall_DoNotCachePossiblyMixedResult()
    {
        var settings = new AppSettings { CustomLlmModel = "old-model" };
        var provider = new Provider
        {
            OnTranslate = () =>
            {
                settings.CustomLlmModel = "temporary-model";
                settings.CustomLlmModel = "old-model";
            }
        };
        var router = Router(provider, settings);
        await router.TranslateBlockAsync("hello", "zh-CN");
        provider.OnTranslate = null;

        Assert.False((await router.TranslateBlockAsync("hello", "zh-CN")).FromCache);
    }

    [Fact]
    public async Task QualityChangedWhileCapturingIdentity_KeyAndRequestUseSameQuality()
    {
        var settings = new AppSettings();
        var reads = 0;
        var provider = new Provider
        {
            OnNameRead = () => { if (++reads == 2) settings.TranslationQuality = TranslationQuality.Best; }
        };
        var result = await Router(provider, settings).TranslateBlockAsync("hello", "zh-CN");
        Assert.Contains(":Best:", result.TargetText);
    }

    [Fact]
    public async Task WordDictionaryEntry_CannotSatisfyBlockRequest()
    {
        var provider = new Provider();
        var router = Router(provider);
        var word = await router.TranslateWordAsync("bank", "zh-CN");
        var block = await router.TranslateBlockAsync("bank", "zh-CN");

        Assert.True(word.FromDictionary);
        Assert.False(block.FromDictionary);
        Assert.Contains(":Block:", block.TargetText);
        Assert.NotEqual(word.NormalizedKey, block.NormalizedKey);
    }

    [Fact]
    public async Task DifferentSourceCapitalization_DoesNotShareTranslation()
    {
        var router = Router(new Provider());
        var upper = await router.TranslateBlockAsync("US", "zh-CN");
        var lower = await router.TranslateBlockAsync("us", "zh-CN");

        Assert.EndsWith(":US", upper.TargetText);
        Assert.EndsWith(":us", lower.TargetText);
        Assert.False(lower.FromCache);
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("provider")]
    [InlineData("credentials")]
    public async Task ChangedTranslationSettings_DoNotReturnPreviousCachedResult(string setting)
    {
        var settings = new AppSettings();
        var router = Router(new Provider(), settings);
        var first = await router.TranslateBlockAsync("hello", "zh-CN");
        switch (setting)
        {
            case "quality": settings.TranslationQuality = TranslationQuality.Best; break;
            case "model": settings.CustomLlmModel = "new-model"; break;
            case "endpoint": settings.CustomLlmBaseUrl = "https://different.example/v1"; break;
            case "provider": settings.TranslationProvider = TranslationProviderKind.QwenMt; break;
            case "credentials": settings.ResolvedApiKey = "test-only-key"; break;
        }
        var second = await router.TranslateBlockAsync("hello", "zh-CN");

        Assert.False(second.FromCache);
        Assert.NotEqual(first.TargetText, second.TargetText);
        Assert.NotEqual(first.NormalizedKey, second.NormalizedKey);
        var repeat = await router.TranslateBlockAsync("hello", "zh-CN");
        Assert.True(repeat.FromCache);
        Assert.Equal(second.TargetText, repeat.TargetText);
        Assert.DoesNotContain("test-only-key", second.NormalizedKey);
    }

    [Fact]
    public async Task LegacyCacheEntry_IsIgnoredBecauseItsSettingsAndModeAreUnknown()
    {
        var l1 = new MemoryLruTranslationCache(100);
        var key = TranslationCacheKey.BuildLegacy("hello", "zh-CN");
        l1.Add(key, new TranslationResult(key, "hello", "旧译文", "zh-CN"));

        var result = await Router(new Provider(), l1: l1).TranslateWordAsync("hello", "zh-CN");

        Assert.False(result.FromCache);
        Assert.NotEqual("旧译文", result.TargetText);
    }

    [Fact]
    public async Task L2Cache_RetainsModeAndSettingsIsolationAcrossRouterInstances()
    {
        // An in-memory SQLite database exercises real serialization and lookup without disk state.
        using var l2 = new SqliteTranslationCache(":memory:", 100, NullLogger<SqliteTranslationCache>.Instance);
        var provider = new Provider();
        var settings = new AppSettings();
        var word = await Router(provider, settings, l2: l2).TranslateWordAsync("hello", "zh-CN");
        var block = await Router(provider, settings, l2: l2).TranslateBlockAsync("hello", "zh-CN");
        Assert.Contains(":Block:", block.TargetText);
        Assert.NotEqual(word.NormalizedKey, block.NormalizedKey);

        settings.CustomLlmModel = "another-model";
        var changed = await Router(provider, settings, l2: l2).TranslateBlockAsync("hello", "zh-CN");
        Assert.False(changed.FromCache);
        Assert.NotEqual(block.TargetText, changed.TargetText);

        var repeat = await Router(provider, settings, l2: l2).TranslateBlockAsync("hello", "zh-CN");
        Assert.True(repeat.FromCache);
        Assert.Equal(changed.TargetText, repeat.TargetText);
    }
}
