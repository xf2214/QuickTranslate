using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class QwenMtProviderHttpStubTests
{
    private static QwenMtTranslationProvider CreateProviderWithHandler(
        DelegatingHandler handler,
        Action<QwenMtOptions>? configure = null)
    {
        var opts = new QwenMtOptions
        {
            MockMode = false,
            ApiKey = "sk-test-valid-key",
            UseStreaming = true,
            Timeout = TimeSpan.FromSeconds(5),
            BaseAddress = "https://mock.test.local/"
        };
        configure?.Invoke(opts);

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(opts.BaseAddress.EndsWith('/') ? opts.BaseAddress : opts.BaseAddress + "/"),
            Timeout = opts.Timeout,
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
    public async Task Case_401_ThrowsTranslationException_AuthFailed()
    {
        var handler = new LambdaHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"Invalid API key\"}", Encoding.UTF8, "application/json")
            }));

        var provider = CreateProviderWithHandler(handler);
        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest("Hello"), CancellationToken.None)) { }
        };

        var ex = await Assert.ThrowsAsync<TranslationException>(action);
        Assert.Equal(TranslationErrorCode.AuthFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task Case_500_ThrowsUnknown_AndDoesNotCrash()
    {
        var handler = new LambdaHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom")
            }));

        var provider = CreateProviderWithHandler(handler);
        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest("Hi"), CancellationToken.None)) { }
        };

        var ex = await Assert.ThrowsAsync<TranslationException>(action);
        Assert.Equal(TranslationErrorCode.Unknown, ex.ErrorCode);
    }

    [Fact]
    public async Task Case_Timeout_ThrowsTimeout()
    {
        var handler = new LambdaHandler(async (_, ct) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = CreateProviderWithHandler(handler, o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(80);
        });

        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest("hi"), CancellationToken.None)) { }
        };

        var ex = await Assert.ThrowsAsync<TranslationException>(action);
        Assert.Equal(TranslationErrorCode.Timeout, ex.ErrorCode);
    }

    [Fact]
    public async Task Case_NetworkFailure_ThrowsNetworkUnavailable()
    {
        var socketEx = new SocketException((int)SocketError.HostNotFound);
        var handler = new LambdaHandler((_, _) =>
            throw new HttpRequestException("No such host", socketEx));

        var provider = CreateProviderWithHandler(handler);
        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest("x"), CancellationToken.None)) { }
        };

        var ex = await Assert.ThrowsAsync<TranslationException>(action);
        Assert.Equal(TranslationErrorCode.NetworkUnavailable, ex.ErrorCode);
    }

    [Fact]
    public async Task Case_OK_200_Streaming_YieldsDeltas_AndFinal()
    {
        var sse = new StringBuilder();
        sse.Append("data: {\"output\":{\"output_text\":\"H\"}}\n\n");
        sse.Append("data: {\"output\":{\"output_text\":\"e\"}}\n\n");
        sse.Append("data: {\"output\":{\"output_text\":\"ll\"}}\n\n");
        sse.Append("data: {\"output\":{\"output_text\":\"o world\"}}\n\n");
        sse.Append("data: [DONE]\n\n");

        var content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream");
        var handler = new LambdaHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        var provider = CreateProviderWithHandler(handler);

        var deltas = new List<string>();
        string? finalFull = null;
        await foreach (var c in provider.TranslateAsync(MakeRequest("Hi"), CancellationToken.None))
        {
            if (!string.IsNullOrEmpty(c.TextDelta))
                deltas.Add(c.TextDelta);
            if (c.IsFinal)
                finalFull = c.FullTranslation;
        }

        Assert.Equal("Hello world", string.Join("", deltas));
        Assert.NotNull(finalFull);
        Assert.Equal("Hello world", finalFull);
    }

    [Fact]
    public async Task ApiKey_ChangedInAppSettings_TakesEffectWithoutRestart()
    {
        var sse = "data: {\"output\":{\"output_text\":\"ok\"}}\n\ndata: [DONE]\n\n";
        var capturedKeys = new List<string?>();
        var handler = new LambdaHandler((req, _) =>
        {
            capturedKeys.Add(req.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        });

        // 模拟设置窗口保存后就地更新同一 AppSettings 实例
        var appSettings = new AppSettings { ResolvedApiKey = "key-before" };
        var opts = new QwenMtOptions
        {
            MockMode = false,
            ApiKey = "key-before",
            UseStreaming = true,
            Timeout = TimeSpan.FromSeconds(5),
            BaseAddress = "https://mock.test.local/"
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri(opts.BaseAddress) };
        var provider = new QwenMtTranslationProvider(
            http, Options.Create(opts), NullLogger<QwenMtTranslationProvider>.Instance, Options.Create(appSettings));

        await foreach (var _ in provider.TranslateAsync(MakeRequest("a"), CancellationToken.None)) { }

        appSettings.ResolvedApiKey = "key-after";

        await foreach (var _ in provider.TranslateAsync(MakeRequest("b"), CancellationToken.None)) { }

        Assert.Equal(new[] { "key-before", "key-after" }, capturedKeys);
    }
}
