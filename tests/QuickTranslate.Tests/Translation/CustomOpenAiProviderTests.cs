using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class CustomOpenAiProviderTests
{
    private static AppSettings ConfiguredSettings(string baseUrl = "https://mock.test.local/v1", string model = "gpt-test") => new()
    {
        CustomLlmBaseUrl = baseUrl,
        CustomLlmModel = model,
        ResolvedApiKey = "sk-test"
    };

    private static CustomOpenAiTranslationProvider CreateProvider(
        AppSettings settings,
        DelegatingHandler? handler = null,
        TimeSpan? timeout = null)
    {
        var http = handler != null ? new HttpClient(handler) : new HttpClient();
        return new CustomOpenAiTranslationProvider(
            http, Options.Create(settings),
            NullLogger<CustomOpenAiTranslationProvider>.Instance, timeout);
    }

    private static TranslationRequest MakeRequest(string text = "Hello") =>
        new(text, null, "auto", "zh", TranslationMode.Block, Guid.NewGuid(), TranslationQuality.Fast);

    private class LambdaHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _func;
        public LambdaHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> func) { _func = func; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _func(request, cancellationToken);
    }

    [Fact]
    public void BuildChatCompletionsUrl_HandlesTrailingSlash_MissingScheme()
    {
        Assert.Equal("https://api.openai.com/v1/chat/completions",
            CustomOpenAiTranslationProvider.BuildChatCompletionsUrl("https://api.openai.com/v1").ToString());
        Assert.Equal("https://api.openai.com/v1/chat/completions",
            CustomOpenAiTranslationProvider.BuildChatCompletionsUrl("https://api.openai.com/v1/").ToString());
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions",
            CustomOpenAiTranslationProvider.BuildChatCompletionsUrl("127.0.0.1:11434/v1").ToString());
    }

    [Fact]
    public async Task NotConfigured_ServesMockStream_WithoutHttp()
    {
        var provider = CreateProvider(new AppSettings()); // 无 baseUrl/model/key
        var chunks = new List<TranslationChunk>();
        await foreach (var c in provider.TranslateAsync(MakeRequest("hello"), CancellationToken.None))
        {
            chunks.Add(c);
        }

        Assert.NotEmpty(chunks);
        Assert.Contains("自定义", provider.Name, StringComparison.Ordinal);
        Assert.Contains("hello", chunks[0].TextDelta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingApiKey_ServesMockStream()
    {
        var s = ConfiguredSettings();
        s.ResolvedApiKey = null;
        var provider = CreateProvider(s);
        var chunks = new List<TranslationChunk>();
        await foreach (var c in provider.TranslateAsync(MakeRequest(), CancellationToken.None))
        {
            chunks.Add(c);
        }
        Assert.Single(chunks);
        Assert.True(chunks[0].IsFinal);
    }

    [Fact]
    public async Task SseStream_YieldsDeltas_AndFinal()
    {
        var sse = new StringBuilder();
        sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n");
        sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n");
        sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"世界\"}}]}\n\n");
        sse.Append("data: [DONE]\n\n");

        string? seenUrl = null;
        string? seenAuth = null;
        string? seenBody = null;
        var handler = new LambdaHandler((req, _) =>
        {
            seenUrl = req.RequestUri!.ToString();
            seenAuth = req.Headers.Authorization?.Parameter;
            seenBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
            });
        });

        var provider = CreateProvider(ConfiguredSettings(), handler);
        var deltas = new List<string>();
        string? finalFull = null;
        await foreach (var c in provider.TranslateAsync(MakeRequest("Hello"), CancellationToken.None))
        {
            if (!string.IsNullOrEmpty(c.TextDelta)) deltas.Add(c.TextDelta);
            if (c.IsFinal) finalFull = c.FullTranslation;
        }

        Assert.Equal("你好世界", string.Join("", deltas));
        Assert.Equal("你好世界", finalFull);

        // 请求形态：Bearer 鉴权、绝对 URL 以 /chat/completions 结尾、body 携带模型与消息
        Assert.Equal("https://mock.test.local/v1/chat/completions", seenUrl);
        Assert.Equal("sk-test", seenAuth);
        Assert.Contains("\"model\":\"gpt-test\"", seenBody);
        Assert.Contains("\"stream\":true", seenBody);
    }

    [Fact]
    public async Task JsonFallback_WhenNotEventStream()
    {
        var handler = new LambdaHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"静态译文\"}}]}",
                Encoding.UTF8, "application/json")
        }));

        var provider = CreateProvider(ConfiguredSettings(), handler);
        var chunks = new List<TranslationChunk>();
        await foreach (var c in provider.TranslateAsync(MakeRequest(), CancellationToken.None))
        {
            chunks.Add(c);
        }

        Assert.Single(chunks);
        Assert.Equal("静态译文", chunks[0].FullTranslation);
    }

    [Fact]
    public async Task Http401_MapsToAuthFailed()
    {
        var handler = new LambdaHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"bad key\"}}", Encoding.UTF8, "application/json")
        }));

        var provider = CreateProvider(ConfiguredSettings(), handler);
        var action = async () =>
        {
            await foreach (var _ in provider.TranslateAsync(MakeRequest(), CancellationToken.None)) { }
        };

        var ex = await Assert.ThrowsAsync<TranslationException>(action);
        Assert.Equal(TranslationErrorCode.AuthFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task SettingsChange_AppliesImmediately_NoRestart()
    {
        var settings = ConfiguredSettings();
        int calls = 0;
        var handler = new LambdaHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"x\"}}]}", Encoding.UTF8, "application/json")
            });
        });

        var provider = CreateProvider(settings, handler);
        await foreach (var _ in provider.TranslateAsync(MakeRequest(), CancellationToken.None)) { }
        Assert.Equal(1, calls);

        // 模拟设置窗口保存：就地清空模型配置 → 下一次请求走 Mock（无 HTTP）
        settings.CustomLlmModel = "";
        await foreach (var _ in provider.TranslateAsync(MakeRequest(), CancellationToken.None)) { }
        Assert.Equal(1, calls); // 未发出第二次 HTTP
    }
}

public class TranslationProviderDispatcherTests
{
    private static TranslationProviderDispatcher CreateDispatcher(AppSettings settings)
    {
        var custom = new CustomOpenAiTranslationProvider(
            new HttpClient(), Options.Create(settings),
            NullLogger<CustomOpenAiTranslationProvider>.Instance);
        var qwen = new QwenMtTranslationProvider(
            new HttpClient(),
            Options.Create(new QwenMtOptions { MockMode = true, ApiKey = "" }),
            NullLogger<QwenMtTranslationProvider>.Instance);
        return new TranslationProviderDispatcher(
            custom, qwen, Options.Create(settings),
            NullLogger<TranslationProviderDispatcher>.Instance);
    }

    private static async Task<string> CollectAsync(ITranslationProvider provider)
    {
        var req = new TranslationRequest("Hello", null, "auto", "zh", TranslationMode.Word);
        var sb = new StringBuilder();
        await foreach (var c in provider.TranslateAsync(req, CancellationToken.None))
        {
            sb.Append(c.FullTranslation ?? c.TextDelta);
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Routes_Custom_ByDefault_AndQwen_AfterHotSwitch()
    {
        var settings = new AppSettings(); // 默认 CustomOpenAi
        var dispatcher = CreateDispatcher(settings);

        // 自定义模型未配置 → mock 输出带"自定义模型未配置"标记
        var asCustom = await CollectAsync(dispatcher);
        Assert.Contains("自定义模型未配置", asCustom, StringComparison.Ordinal);

        // 模拟设置窗口保存：切到 QwenMt → 立即路由到 QwenMt mock（"[译文]"标记）
        settings.TranslationProvider = TranslationProviderKind.QwenMt;
        var asQwen = await CollectAsync(dispatcher);
        Assert.Contains("[译文]", asQwen, StringComparison.Ordinal);

        // 切回自定义
        settings.TranslationProvider = TranslationProviderKind.CustomOpenAi;
        var back = await CollectAsync(dispatcher);
        Assert.Contains("自定义模型未配置", back, StringComparison.Ordinal);
    }
}
