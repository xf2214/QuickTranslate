using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

/// <summary>
/// 提示词档位（默认 + Fast/Balanced/Best）与模型连通性测试功能。
/// </summary>
public class PromptTierTests
{
    private static TranslationRequest Req(TranslationQuality q, TranslationMode mode = TranslationMode.Block) =>
        new("Hello", null, "auto", "zh", mode, Guid.NewGuid(), q);

    [Fact]
    public void Balanced_UsesDefaultPrompt()
    {
        var p = CustomOpenAiTranslationProvider.BuildSystemPrompt(Req(TranslationQuality.Balanced));
        Assert.StartsWith("You are a professional translator.", p);
        Assert.Contains("Simplified Chinese", p);
        Assert.Contains("Output ONLY the translation.", p);
    }

    [Fact]
    public void Fast_UsesConciseTierPrompt()
    {
        var p = CustomOpenAiTranslationProvider.BuildSystemPrompt(Req(TranslationQuality.Fast));
        Assert.StartsWith("You are a fast translation engine.", p);
        Assert.Contains("Be brief and direct.", p);
    }

    [Fact]
    public void Best_UsesQualityTierPrompt()
    {
        var p = CustomOpenAiTranslationProvider.BuildSystemPrompt(Req(TranslationQuality.Best));
        Assert.StartsWith("You are an expert professional translator.", p);
        Assert.Contains("accurate, natural and fluent", p);
    }

    [Fact]
    public void Tiers_AreDistinct()
    {
        var fast = CustomOpenAiTranslationProvider.BuildSystemPrompt(Req(TranslationQuality.Fast));
        var balanced = CustomOpenAiTranslationProvider.BuildSystemPrompt(Req(TranslationQuality.Balanced));
        var best = CustomOpenAiTranslationProvider.BuildSystemPrompt(Req(TranslationQuality.Best));
        Assert.NotEqual(fast, balanced);
        Assert.NotEqual(balanced, best);
        Assert.NotEqual(fast, best);
    }

    [Fact]
    public void WordMode_KeepsWordHint_AcrossAllTiers()
    {
        foreach (var q in new[] { TranslationQuality.Fast, TranslationQuality.Balanced, TranslationQuality.Best })
        {
            var p = CustomOpenAiTranslationProvider.BuildSystemPrompt(Req(q, TranslationMode.Word));
            Assert.Contains("single word or short phrase", p);
        }
    }

    [Theory]
    [InlineData(TranslationQuality.Fast, 0.0)]
    [InlineData(TranslationQuality.Balanced, 0.1)]
    [InlineData(TranslationQuality.Best, 0.3)]
    public void TemperatureByTier(TranslationQuality q, double expected)
    {
        Assert.Equal(expected, CustomOpenAiTranslationProvider.TemperatureFor(q));
    }
}

public class ModelConnectionTestTests
{
    private static CustomOpenAiTranslationProvider CreateProvider(DelegatingHandler? handler = null)
    {
        var http = handler != null ? new HttpClient(handler) : new HttpClient();
        return new CustomOpenAiTranslationProvider(
            http, Options.Create(new AppSettings()),
            NullLogger<CustomOpenAiTranslationProvider>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("", "gpt-test", "sk-x")]
    [InlineData("https://mock.test.local/v1", "", "sk-x")]
    [InlineData("https://mock.test.local/v1", "gpt-test", "")]
    public async Task MissingConfig_FailsWithoutHttp(string baseUrl, string model, string apiKey)
    {
        int calls = 0;
        var handler = new LambdaHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse("{}"));
        });

        var result = await CreateProvider(handler).TestConnectionAsync(baseUrl, model, apiKey);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ValidResponse_ReturnsSuccessWithLatency()
    {
        var handler = new LambdaHandler((_, _) => Task.FromResult(
            JsonResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}")));

        var result = await CreateProvider(handler)
            .TestConnectionAsync("https://mock.test.local/v1", "gpt-test", "sk-test");

        Assert.True(result.Success);
        Assert.NotNull(result.Latency);
        Assert.True(result.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task AuthFailure_ReturnsFriendlyMessage()
    {
        var handler = new LambdaHandler((_, _) =>
            Task.FromResult(JsonResponse("{\"error\":{\"message\":\"invalid key\"}}", HttpStatusCode.Unauthorized)));

        var result = await CreateProvider(handler)
            .TestConnectionAsync("https://mock.test.local/v1", "gpt-test", "sk-bad");

        Assert.False(result.Success);
        Assert.Contains("API Key", result.Message);
    }

    [Fact]
    public async Task ServerErrorPayload_ReturnsServerMessage()
    {
        var handler = new LambdaHandler((_, _) =>
            Task.FromResult(JsonResponse("{\"error\":{\"message\":\"model not found\"}}")));

        var result = await CreateProvider(handler)
            .TestConnectionAsync("https://mock.test.local/v1", "ghost-model", "sk-test");

        Assert.False(result.Success);
        Assert.Contains("model not found", result.Message);
    }

    [Fact]
    public async Task EmptyModelContent_Fails()
    {
        var handler = new LambdaHandler((_, _) =>
            Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")));

        var result = await CreateProvider(handler)
            .TestConnectionAsync("https://mock.test.local/v1", "gpt-test", "sk-test");

        Assert.False(result.Success);
    }

    private class LambdaHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _func;
        public LambdaHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> func) { _func = func; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _func(request, cancellationToken);
    }
}
