using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class CountingProvider : ITranslationProvider
{
    private int _callCount;
    public int CallCount => _callCount;
    public string Name => "Counting";

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);
        var text = $"translated-{request.Text}";
        await Task.Yield();
        yield return new TranslationChunk(text, IsFinal: true, FullTranslation: text);
    }
}

public class RouterProviderIntegrationTests
{
    private static DefaultTranslationRouter BuildRouter(
        ITranslationCache cache,
        ITranslationProvider provider,
        ILocalDictionary? dict = null,
        TranslationQuality quality = TranslationQuality.Fast)
    {
        dict ??= Substitute.For<ILocalDictionary>();
        dict.TryLookup(Arg.Any<string>(), Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x => { x[2] = default!; return false; });

        var settings = Options.Create(new AppSettings { TranslationQuality = quality });
        var logger = NullLogger<DefaultTranslationRouter>.Instance;
        return new DefaultTranslationRouter(cache, dict, provider, settings, logger);
    }

    private static string NKey(string word, string lang)
        => $"{word.Trim().ToLowerInvariant()}||{lang.Trim().ToLowerInvariant()}";

    [Fact]
    public async Task TranslateWordAsync_L1CacheHit_DoesNotCallProvider()
    {
        var cache = new MemoryLruTranslationCache(100);
        var provider = new CountingProvider();
        var router = BuildRouter(cache, provider);

        var key = NKey("hello", "zh-CN");
        var cached = new TranslationResult(
            NormalizedKey: key,
            SourceText: "hello",
            TargetText: "你好",
            TargetLanguage: "zh-CN",
            FromCache: false);
        cache.Add(key, cached);

        Assert.Equal(0, provider.CallCount);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.True(result.FromCache);
        Assert.Equal("你好", result.TargetText);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task TranslateBlockAsync_FirstCall_InvokesProvider_AndWritesToCache()
    {
        var cache = new MemoryLruTranslationCache(100);
        var provider = new CountingProvider();
        var router = BuildRouter(cache, provider);

        var first = await router.TranslateBlockAsync("Hello world", "zh-CN");

        Assert.True(provider.CallCount >= 1);
        Assert.Contains("Hello world", first.TargetText, StringComparison.Ordinal);
        Assert.NotNull(first.TargetText);

        var countAfterFirst = provider.CallCount;
        var second = await router.TranslateBlockAsync("Hello world", "zh-CN");

        Assert.True(second.FromCache);
        Assert.Equal(first.TargetText, second.TargetText);
        Assert.Equal(countAfterFirst, provider.CallCount);
    }
}
