using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class QwenCancelPropagationTests
{
    private static QwenMtTranslationProvider CreateProviderWithHandler(
        DelegatingHandler handler,
        Action<QwenMtOptions>? configure = null)
    {
        var opts = new QwenMtOptions
        {
            MockMode = false,
            ApiKey = "sk-test-valid-key",
            UseStreaming = false,
            Timeout = TimeSpan.FromSeconds(5),
            BaseAddress = "https://mock.test.local/"
        };
        configure?.Invoke(opts);

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(opts.BaseAddress.EndsWith('/') ? opts.BaseAddress : opts.BaseAddress + "/"),
        };

        return new QwenMtTranslationProvider(http, Options.Create(opts), NullLogger<QwenMtTranslationProvider>.Instance);
    }

    private static TranslationRequest MakeRequest(string text = "test")
        => new(text, null, "auto", "zh", TranslationMode.Block, Guid.NewGuid(), QuickTranslate.Core.Options.TranslationQuality.Fast);

    private class LambdaHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _func;
        public LambdaHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> func) { _func = func; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _func(request, cancellationToken);
    }

    [Fact]
    public async Task ManualCancellationToken_NotMappedToTimeout()
    {
        var handler = new LambdaHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var userCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var provider = CreateProviderWithHandler(handler, o =>
        {
            o.Timeout = TimeSpan.FromSeconds(10);
        });

        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest("hi"), userCts.Token)) { }
        };

        var ex = await Record.ExceptionAsync(action);
        Assert.NotNull(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<TranslationException>(ex);
    }

    [Fact]
    public async Task InternalTimeout_MappedToTranslationExceptionTimeout()
    {
        var handler = new LambdaHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = CreateProviderWithHandler(handler, o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(20);
        });

        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest("hi"), CancellationToken.None)) { }
        };

        var ex = await Assert.ThrowsAsync<TranslationException>(action);
        Assert.Equal(TranslationErrorCode.Timeout, ex.ErrorCode);
    }
}
