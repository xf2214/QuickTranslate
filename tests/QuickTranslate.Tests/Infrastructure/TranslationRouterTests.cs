using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class TranslationRouterTests
{
    private static TranslationResult MakeOnlineResult(string src, string targetLang) => new(
        NormalizedKey: src.ToLowerInvariant(),
        SourceText: src,
        TargetText: $"online-{src}",
        TargetLanguage: targetLang,
        FromCache: false,
        FromDictionary: false,
        NeedsOnline: false);

    private static string NKey(string word, string lang) => $"{word.Trim().ToLowerInvariant()}||{lang.Trim().ToLowerInvariant()}";

    [Fact]
    public async Task Case1_CacheHit_DoesNotCallDictOrProvider()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();
        var logger = NullLogger<DefaultTranslationRouter>.Instance;

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

        var router = new DefaultTranslationRouter(cache, dict, provider, logger);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.True(result.FromCache);
        Assert.Equal("你好", result.TargetText);
        _ = dict.DidNotReceiveWithAnyArgs().TryLookup(default!, default!, out _);
        _ = await provider.DidNotReceiveWithAnyArgs().TranslateWordAsync(default!, default!, default);
    }

    [Fact]
    public async Task Case2_CacheMissDictHit_DoesNotCallProvider_AndAddsToCache()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();
        var logger = NullLogger<DefaultTranslationRouter>.Instance;

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

        var router = new DefaultTranslationRouter(cache, dict, provider, logger);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.True(result.FromDictionary);
        Assert.False(result.FromCache);
        Assert.Equal("字典你好", result.TargetText);

        _ = await provider.DidNotReceiveWithAnyArgs().TranslateWordAsync(default!, default!, default);
        cache.Received(1).Add(NKey("hello", "zh-CN"), Arg.Any<TranslationResult>());
    }

    [Fact]
    public async Task Case3_CacheAndDictMiss_CallsProvider_AndAddsToCache()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();
        var logger = NullLogger<DefaultTranslationRouter>.Instance;

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

        var online = MakeOnlineResult("hello", "zh-CN");
        provider.TranslateWordAsync("Hello", "zh-CN", Arg.Any<CancellationToken>())
            .Returns(online);

        var router = new DefaultTranslationRouter(cache, dict, provider, logger);
        var result = await router.TranslateWordAsync("Hello", "zh-CN");

        Assert.False(result.FromCache);
        Assert.False(result.FromDictionary);
        Assert.Equal("online-hello", result.TargetText);

        _ = await provider.Received(1).TranslateWordAsync("Hello", "zh-CN", Arg.Any<CancellationToken>());
        cache.Received(1).Add(NKey("hello", "zh-CN"), Arg.Any<TranslationResult>());
    }

    [Fact]
    public async Task Case4_BlockSkipsDictionary_GoesCacheThenProvider()
    {
        var cache = Substitute.For<ITranslationCache>();
        var dict = Substitute.For<ILocalDictionary>();
        var provider = Substitute.For<ITranslationProvider>();
        var logger = NullLogger<DefaultTranslationRouter>.Instance;

        cache.TryGet(Arg.Any<string>(), out Arg.Any<TranslationResult>()!)
            .Returns(x =>
            {
                x[1] = default!;
                return false;
            });

        var blockOnline = new TranslationResult(
            NormalizedKey: NKey("block text here", "zh-CN"),
            SourceText: "block text here",
            TargetText: "在线-block",
            TargetLanguage: "zh-CN");
        provider.TranslateBlockAsync("block text here", "zh-CN", Arg.Any<CancellationToken>())
            .Returns(blockOnline);

        var router = new DefaultTranslationRouter(cache, dict, provider, logger);
        var result = await router.TranslateBlockAsync("block text here", "zh-CN");

        Assert.Equal("在线-block", result.TargetText);
        _ = dict.DidNotReceiveWithAnyArgs().TryLookup(default!, default!, out _);
        _ = await provider.Received(1).TranslateBlockAsync("block text here", "zh-CN", Arg.Any<CancellationToken>());
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
}
