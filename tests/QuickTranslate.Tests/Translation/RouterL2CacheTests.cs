using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class RouterL2CacheTests
{
    private static string NKey(string word, string lang) => $"{word.Trim().ToLowerInvariant()}||{lang.Trim().ToLowerInvariant()}";

    private static IAsyncEnumerable<TranslationChunk> SingleChunkStream(string targetText)
    {
        return SingleChunkStreamImpl(targetText);
    }

    private static async IAsyncEnumerable<TranslationChunk> SingleChunkStreamImpl(string targetText)
    {
        await Task.Yield();
        yield return new TranslationChunk(TextDelta: "", IsFinal: true, FullTranslation: targetText);
    }

    private static IOptions<AppSettings> TestSettings =>
        Options.Create(new AppSettings { TranslationQuality = TranslationQuality.Fast });

    [Fact]
    public async Task Case1_L1Miss_L2Hit_DoesNotCallProvider()
    {
        var l1Cache = Substitute.For<ITranslationCache>();
        var l2Cache = Substitute.For<ITranslationL2Cache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();
        var logger = NullLogger<DefaultTranslationRouter>.Instance;

        var key = NKey("hello", "zh-CN");
        var l2Result = new TranslationResult(
            NormalizedKey: key,
            SourceText: "hello",
            TargetText: "你好L2",
            TargetLanguage: "zh-CN",
            FromCache: false,
            FromDictionary: false);

        l1Cache.TryGet(Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x => { x[1] = default!; return false; });

        l2Cache.TryGetAsync(key, Arg.Any<CancellationToken>())
            .Returns(l2Result);

        var router = new DefaultTranslationRouter(l1Cache, dict, provider, TestSettings, logger, l2Cache);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.True(result.FromCache);
        Assert.Equal("你好L2", result.TargetText);

        _ = dict.DidNotReceiveWithAnyArgs().TryLookup(default!, default!, out _);
        _ = provider.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default);
        l1Cache.Received(1).Add(key, Arg.Is<TranslationResult>(r => r.TargetText == "你好L2"));
    }

    [Fact]
    public async Task Case2_L1Miss_L2Miss_ProviderCalled_ThenBothCacheWritten()
    {
        var key = NKey("newterm", "zh-CN");
        var settings = TestSettings;
        var logger = NullLogger<DefaultTranslationRouter>.Instance;
        int providerCallCount = 0;

        static IAsyncEnumerable<TranslationChunk> ProviderStream(string text)
        {
            return SingleChunkStreamImpl($"在线-{text}");
        }

        var dict = Substitute.For<ILocalDictionary>();
        dict.TryLookup(Arg.Any<string>(), Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x => { x[2] = default!; return false; });

        var provider = Substitute.For<ITranslationProvider>();
        provider.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref providerCallCount);
                return ProviderStream("newterm");
            });

        var l1Cache1 = new MemoryLruTranslationCache(capacity: 100);
        var l2Cache = Substitute.For<ITranslationL2Cache>();
        TranslationResult? capturedL2Write = null;
        l2Cache.TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TranslationResult?)null);
        l2Cache.When(c => c.AddAsync(Arg.Any<string>(), Arg.Any<TranslationResult>(), Arg.Any<CancellationToken>()))
            .Do(call => capturedL2Write = call.ArgAt<TranslationResult>(1));

        var router1 = new DefaultTranslationRouter(l1Cache1, dict, provider, settings, logger, l2Cache);
        var r1 = await router1.TranslateWordAsync("newterm", "zh-CN");

        Assert.Equal("在线-newterm", r1.TargetText);
        Assert.Equal(1, providerCallCount);
        Assert.NotNull(capturedL2Write);
        Assert.Equal("在线-newterm", capturedL2Write.TargetText);
        var l1Has = l1Cache1.TryGet(key, out var l1Check);
        Assert.True(l1Has);
        Assert.Equal("在线-newterm", l1Check.TargetText);

        var r2 = await router1.TranslateWordAsync("newterm", "zh-CN");
        Assert.Equal("在线-newterm", r2.TargetText);
        Assert.True(r2.FromCache);
        Assert.Equal(1, providerCallCount);

        l2Cache = Substitute.For<ITranslationL2Cache>();
        l2Cache.TryGetAsync(key, Arg.Any<CancellationToken>())
            .Returns(capturedL2Write);

        var l1Cache2 = new MemoryLruTranslationCache(capacity: 100);
        var provider2 = Substitute.For<ITranslationProvider>();
        var router2 = new DefaultTranslationRouter(l1Cache2, dict, provider2, settings, logger, l2Cache);

        var r3 = await router2.TranslateWordAsync("newterm", "zh-CN");
        Assert.True(r3.FromCache);
        Assert.Equal("在线-newterm", r3.TargetText);
        Assert.Equal(1, providerCallCount);
        _ = provider2.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default);
    }
}
