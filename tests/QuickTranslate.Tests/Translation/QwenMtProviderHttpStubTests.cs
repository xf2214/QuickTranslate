using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
    public async Task Case_OK_200_Json_YieldsFinalTranslation()
    {
        var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello world\"},\"finish_reason\":\"stop\"}]}";
        var content = new StringContent(json, Encoding.UTF8, "application/json");
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
    public async Task Request_Body_MatchesQwenMtContract_AndMapsLanguageNames()
    {
        string? capturedUrl = null;
        string? capturedBody = null;
        var handler = new LambdaHandler(async (req, _) =>
        {
            capturedUrl = req.RequestUri?.ToString();
            capturedBody = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var provider = CreateProviderWithHandler(handler);
        // target 传 zh-CN：官方 translation_options 要求英文全称 Chinese
        var req = new TranslationRequest("Hello", null, "auto", "zh-CN",
            TranslationMode.Word, Guid.NewGuid(), TranslationQuality.Balanced);
        await foreach (var _ in provider.TranslateAsync(req, CancellationToken.None)) { }

        Assert.NotNull(capturedUrl);
        Assert.EndsWith("/chat/completions", capturedUrl);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody);
        var root = doc.RootElement;
        // Balanced 档 → qwen-mt-flash
        Assert.Equal("qwen-mt-flash", root.GetProperty("model").GetString());
        // messages 仅一条 user 消息，content 为待翻译文本
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[0].GetProperty("content").GetString());
        // 语种映射：auto 透传，zh-CN → Chinese
        var to = root.GetProperty("translation_options");
        Assert.Equal("auto", to.GetProperty("source_lang").GetString());
        Assert.Equal("Chinese", to.GetProperty("target_lang").GetString());
    }

    [Fact]
    public async Task Case_OK_ButEmptyContent_ThrowsInvalidResponse()
    {
        var handler = new LambdaHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"\"}}]}", Encoding.UTF8, "application/json")
            }));

        var provider = CreateProviderWithHandler(handler);
        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest("x"), CancellationToken.None)) { }
        };

        var ex = await Assert.ThrowsAsync<TranslationException>(action);
        Assert.Equal(TranslationErrorCode.InvalidResponse, ex.ErrorCode);
    }

    [Fact]
    public async Task ApiKey_ChangedInAppSettings_TakesEffectWithoutRestart()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
        var capturedKeys = new List<string?>();
        var handler = new LambdaHandler((req, _) =>
        {
            capturedKeys.Add(req.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        // 模拟设置窗口保存后就地更新同一 AppSettings 实例
        var appSettings = new AppSettings { ResolvedApiKey = "key-before" };
        var opts = new QwenMtOptions
        {
            MockMode = false,
            ApiKey = "key-before",
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
