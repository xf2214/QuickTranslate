using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class TranslationRouterTests
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

    private static DefaultTranslationRouter BuildRouter(
        ITranslationCache cache,
        ILocalDictionary dict,
        ITranslationProvider provider)
    {
        var settings = Options.Create(new AppSettings { TranslationQuality = TranslationQuality.Fast });
        var logger = NullLogger<DefaultTranslationRouter>.Instance;
        return new DefaultTranslationRouter(cache, dict, provider, settings, logger);
    }

    [Fact]
    public async Task Case1_CacheHit_DoesNotCallDictOrProvider()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();

        var cached = new TranslationResult(
            NormalizedKey: NKey("hello", "zh-CN"),
            SourceText: "hello",
            TargetText: "你好",
            TargetLanguage: "zh-CN",
            FromCache: true,
            FromDictionary: false);

        cache.TryGet(NKey("hello", "zh-CN"), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[1] = cached;
                return true;
            });

        var router = BuildRouter(cache, dict, provider);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.True(result.FromCache);
        Assert.Equal("你好", result.TargetText);
        _ = dict.DidNotReceiveWithAnyArgs().TryLookup(default!, default!, out _);
        _ = provider.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default);
    }

    [Fact]
    public async Task Case2_CacheMissDictHit_DoesNotCallProvider_AndAddsToCache()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();

        cache.TryGet(Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[1] = default!;
                return false;
            });

        var dictResult = new TranslationResult(
            NormalizedKey: NKey("hello", "zh-CN"),
            SourceText: "hello",
            TargetText: "字典你好",
            TargetLanguage: "zh-CN",
            FromCache: false,
            FromDictionary: true);

        dict.TryLookup("Hello", "zh-CN", out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[2] = dictResult;
                return true;
            });

        var router = BuildRouter(cache, dict, provider);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.True(result.FromDictionary);
        Assert.False(result.FromCache);
        Assert.Equal("字典你好", result.TargetText);

        _ = provider.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default);
        cache.Received(1).Add(NKey("hello", "zh-CN"), Arg.Any<TranslationResult>());
    }

    [Fact]
    public async Task Case3_CacheAndDictMiss_CallsProvider_AndAddsToCache()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();

        cache.TryGet(Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[1] = default!;
                return false;
            });

        dict.TryLookup(Arg.Any<string>(), Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[2] = default!;
                return false;
            });

        provider.TranslateAsync(Arg.Is<TranslationRequest>(r => r.Text == "Hello" && r.TargetLanguage == "zh-CN"), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("online-hello"));

        var router = BuildRouter(cache, dict, provider);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.False(result.FromCache);
        Assert.False(result.FromDictionary);
        Assert.Equal("online-hello", result.TargetText);

        _ = provider.Received(1).TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>());
        cache.Received(1).Add(NKey("hello", "zh-CN"), Arg.Any<TranslationResult>());
    }

    [Fact]
    public async Task Case4_BlockSkipsDictionary_GoesCacheThenProvider()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();

        cache.TryGet(Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[1] = default!;
                return false;
            });

        provider.TranslateAsync(Arg.Is<TranslationRequest>(r => r.Text == "block text here"), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("在线-block"));

        var router = BuildRouter(cache, dict, provider);
        var result = await router.TranslateBlockAsync("block text here", "zh-CN");

        Assert.Equal("在线-block", result.TargetText);
        _ = dict.DidNotReceiveWithAnyArgs().TryLookup(default!, default!, out _);
        _ = provider.Received(1).TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>());
        cache.Received(1).Add(NKey("block text here", "zh-CN"), Arg.Any<TranslationResult>());
    }

    [Fact]
    public void Case5_StubMiniDictionary_KnownWords()
    {
        var dict = new StubMiniDictionary();
        Assert.True(dict.Count >= 50);

        Assert.True(dict.TryLookup("Hello", "zh-CN", out var r1));
        Assert.Equal("你好", r1.TargetText);
        Assert.True(r1.FromDictionary);

        Assert.True(dict.TryLookup("translate", "zh-CN", out var r2));
        Assert.Equal("翻译", r2.TargetText);

        Assert.False(dict.TryLookup("supercalifragilistic", "zh-CN", out _));
    }

    [Fact]
    public async Task Case6_OnlineReturnsEmpty_ThrowsInvalidResponse_AndDoesNotCache()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();

        cache.TryGet(Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[1] = default!;
                return false;
            });

        dict.TryLookup(Arg.Any<string>(), Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[2] = default!;
                return false;
            });

        // 在线提供方返回空译文（Final 帧 FullTranslation 为空）
        provider.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream(""));

        var router = BuildRouter(cache, dict, provider);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => router.TranslateWordAsync("someunknownword", "zh-CN"));
        Assert.Equal(TranslationErrorCode.InvalidResponse, ex.ErrorCode);

        // 空结果不得写入缓存，否则同一词后续永远命中空译文
        cache.DidNotReceiveWithAnyArgs().Add(default!, default!);
    }
}
