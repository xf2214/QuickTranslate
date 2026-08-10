using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class QwenMtProviderMockStreamTests
{
    private static QwenMtTranslationProvider CreateProvider(Action<QwenMtOptions>? configure = null)
    {
        var opts = new QwenMtOptions { MockMode = true, MockChunkCount = 4, MockChunkIntervalMs = 0 };
        configure?.Invoke(opts);
        var options = Options.Create(opts);
        var http = new HttpClient();
        var logger = NullLogger<QwenMtTranslationProvider>.Instance;
        return new QwenMtTranslationProvider(http, options, logger);
    }

    private static TranslationRequest MakeRequest(string text = "Hello world test", string targetLang = "zh")
        => new(text, null, "auto", targetLang, TranslationMode.Block, Guid.NewGuid(), TranslationQuality.Fast);

    [Fact]
    public async Task MockMode_FirstChunk_MoveNextAsync_ReturnsFirstDelta_BeforeAllComplete()
    {
        var provider = CreateProvider(o => { o.MockChunkCount = 4; o.MockChunkIntervalMs = 50; });
        var req = MakeRequest("Hello world test string here okay");

        var enumerable = provider.TranslateAsync(req, CancellationToken.None);
        var enumerator = enumerable.GetAsyncEnumerator();

        var moved = await enumerator.MoveNextAsync();
        Assert.True(moved);

        var first = enumerator.Current;
        Assert.NotNull(first);
        Assert.False(string.IsNullOrEmpty(first.TextDelta));
        Assert.False(first.IsFinal);
        Assert.Null(first.FullTranslation);

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task MockMode_LastChunk_IsFinal_And_HasFullTranslation()
    {
        var provider = CreateProvider(o => { o.MockChunkCount = 3; o.MockChunkIntervalMs = 0; });
        var req = MakeRequest("abcdefghij");

        TranslationChunk? finalChunk = null;
        TranslationChunk? prev = null;
        await foreach (var c in provider.TranslateAsync(req, CancellationToken.None))
        {
            if (prev != null) Assert.False(prev.IsFinal);
            prev = c;
            if (c.IsFinal) finalChunk = c;
        }

        Assert.NotNull(finalChunk);
        Assert.True(finalChunk!.IsFinal);
        Assert.NotNull(finalChunk.FullTranslation);
        Assert.NotEmpty(finalChunk.FullTranslation);
    }

    [Fact]
    public async Task MockMode_CancelMidStream_ThrowsOperationCanceled()
    {
        var provider = CreateProvider(o => { o.MockChunkCount = 10; o.MockChunkIntervalMs = 200; });
        var req = MakeRequest(new string('x', 100));

        using var cts = new CancellationTokenSource(120);
        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(req, cts.Token)) { }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
    }
}
