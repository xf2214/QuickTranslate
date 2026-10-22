using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using QuickTranslate.Tests.Infrastructure;
using Xunit;

namespace QuickTranslate.Tests.Logging;

public class RouterPrivacyLoggingTests
{
    [Fact]
    public async Task CacheHit_DebugLogsDoNotContainSensitiveSourceText()
    {
        const string sensitive = "CONFIDENTIAL_OCR_TEXT_123";
        var key = TranslationCacheKey.BuildForRequest(sensitive, "zh-CN", TranslationMode.Word, new AppSettings(), string.Empty);
        var cache = Substitute.For<ITranslationCache>();
        cache.TryGet(key, out Arg.Any<TranslationResult>()!)
            .Returns(call =>
            {
                call[1] = new TranslationResult(key, sensitive, "翻译", "zh-CN", true);
                return true;
            });
        var entries = new List<SpyLogEntry>();
        var router = new DefaultTranslationRouter(
            cache,
            Substitute.For<ILocalDictionary>(),
            Substitute.For<ITranslationProvider>(),
            Options.Create(new AppSettings()),
            new SpyLogger<DefaultTranslationRouter>(entries));
        var result = await router.TranslateWordAsync(sensitive, "zh-CN");
        Assert.True(result.FromCache);
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain(sensitive, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sensitive, string.Join(" ", entry.State.Select(x => x.Value?.ToString())),
                StringComparison.OrdinalIgnoreCase);
        });
    }
}
