using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Translation;

/// <summary>
/// 自定义大模型翻译：任意 OpenAI 兼容 /chat/completions 端点
/// （OpenAI / DeepSeek / Moonshot / 本地 vLLM、Ollama、one-api 等）。
/// 配置（BaseUrl/Model/ApiKey）每次请求都从 AppSettings 单例实时读取，
/// 设置窗口保存后立即生效、无需重启。
/// 未配置 BaseUrl/Model 或缺 API Key 时降级为 Mock 占位流（与 QwenMt 行为一致），
/// 保证开发/演示环境不报错；配置齐全后走真实 HTTP；流式与采样参数可配置，亦支持服务直接返回 JSON。
/// </summary>
public class CustomOpenAiTranslationProvider : ITranslationProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<CustomOpenAiTranslationProvider> _logger;
    private readonly TimeSpan _timeout;

    public string Name => "自定义大模型 (OpenAI 兼容)";

    public CustomOpenAiTranslationProvider(
        HttpClient httpClient,
        IOptions<AppSettings> settings,
        ILogger<CustomOpenAiTranslationProvider> logger,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest req,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var config = _settings.Value.CaptureTranslationRequestSettings();
        var baseUrl = (config.Settings.BaseUrl ?? "").Trim();
        var model = (config.Settings.Model ?? "").Trim();
        var apiKey = (config.ApiKey ?? "").Trim();
        if (baseUrl.Length == 0 || model.Length == 0 || (config.Settings.RequireApiKey && apiKey.Length == 0))
        {
            _logger.LogDebug("Custom LLM not configured; serving mock stream");
            var mock = MockText(req);
            yield return new TranslationChunk(TextDelta: mock, IsFinal: true, FullTranslation: mock);
            yield break;
        }
        using var request = CreateRequest(req, baseUrl, model, apiKey, config.Settings);
        await foreach (var chunk in TranslationHttp.SendAsync(_httpClient, request, _timeout, ct).ConfigureAwait(false))
            yield return chunk;
    }

    public static string MockText(TranslationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return "[示例]";
        var isChinese = req.Text.Any(c => c >= 0x4e00 && c <= 0x9fff);
        if (req.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return isChinese ? req.Text : $"[自定义模型未配置·演示]{req.Text}";
        }
        return isChinese ? $"[Demo]{req.Text}" : req.Text;
    }

    /// <summary>
    /// 按质量档位选择系统提示词：Balanced 档即内置默认提示词，
    /// Fast 档精简求快，Best 档强调准确、自然、风格保真。
    /// </summary>
    public static string BuildSystemPrompt(TranslationRequest req)
    {
        var lang = LanguageName(req.TargetLanguage);
        var modeHint = req.Mode == TranslationMode.Word
            ? "The input is a single word or short phrase: give its most common translation, optionally followed by a brief gloss in parentheses. "
            : "Preserve paragraph breaks. ";

        return req.Quality switch
        {
            TranslationQuality.Fast =>
                $"You are a fast translation engine. Translate the user's text into {lang}. {modeHint}" +
                "Be brief and direct. Output ONLY the translation. No explanations, no quotes, no extra formatting.",
            TranslationQuality.Best =>
                $"You are an expert professional translator. Translate the user's text into {lang}. {modeHint}" +
                "Ensure the translation is accurate, natural and fluent, preserving the original tone, style, terminology and formatting. " +
                "Output ONLY the translation. No explanations, no quotes, no extra formatting.",
            _ => // Balanced = 默认提示词
                "You are a professional translator. Translate the user's text into " +
                $"{lang}. {modeHint}" +
                "Output ONLY the translation. No explanations, no quotes, no extra formatting."
        };
    }

    /// <summary>各档位采样温度：Fast 最确定、Best 略放宽以换取更自然的表达。</summary>
    public static double TemperatureFor(TranslationQuality quality) => quality switch
    {
        TranslationQuality.Fast => 0.0,
        TranslationQuality.Best => 0.3,
        _ => 0.1
    };

    internal static string LanguageName(string lang)
    {
        var l = (lang ?? string.Empty).ToLowerInvariant();
        if (l.StartsWith("zh")) return "Simplified Chinese";
        if (l.StartsWith("en")) return "English";
        if (l.StartsWith("ja")) return "Japanese";
        if (l.StartsWith("ko")) return "Korean";
        if (l.StartsWith("de")) return "German";
        if (l.StartsWith("fr")) return "French";
        if (l.StartsWith("es")) return "Spanish";
        if (l.StartsWith("ru")) return "Russian";
        return lang ?? string.Empty;
    }

    // Retain the existing internal seam for callers while sharing request and transport policy.
    internal async IAsyncEnumerable<TranslationChunk> RealTranslateAsync(TranslationRequest req,
        string baseUrl, string model, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = _settings.Value.CaptureTranslationSettings();
        using var request = CreateRequest(req, baseUrl, model, apiKey, settings);
        await foreach (var chunk in TranslationHttp.SendAsync(_httpClient, request, _timeout, ct).ConfigureAwait(false))
            yield return chunk;
    }

    private HttpRequestMessage CreateRequest(TranslationRequest req, string baseUrl, string model,
        string apiKey, AppSettings.TranslationSettingsSnapshot settings)
    {
        // HttpClient merges default headers after constructing this message. Fail closed rather than
        // mutating shared defaults or allowing an anonymous request to inherit credentials.
        if (!settings.RequireApiKey && _httpClient.DefaultRequestHeaders.Contains("Authorization"))
            throw TranslationException.BadRequest("匿名模式不能使用带默认鉴权头的 HTTP 客户端");
        var body = new OpenAiChatRequest
        {
            Model = model,
            Messages = new List<OpenAiChatMessage>
            {
                new() { Role = "system", Content = BuildSystemPrompt(req) },
                new() { Role = "user", Content = req.Text }
            },
            Stream = settings.UseStreaming,
            Temperature = settings.SendTemperature ? TemperatureFor(req.Quality) : null
        };
        var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUrl(baseUrl));
        if (settings.RequireApiKey)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Probe with the current authentication/stream/parameter policy and the unsaved endpoint/model.</summary>
    public Task<ModelTestResult> TestConnectionAsync(string baseUrl, string model, string apiKey, CancellationToken ct = default)
        => TestConnectionAsync(baseUrl, model, apiKey, _settings.Value.CaptureTranslationSettings(), ct);

    public Task<ModelTestResult> TestConnectionAsync(string baseUrl, string model, string apiKey,
        bool requireApiKey, bool useStreaming, bool sendTemperature, CancellationToken ct = default)
        => TestConnectionAsync(baseUrl, model, apiKey,
            _settings.Value.CaptureTranslationSettings() with
            { RequireApiKey = requireApiKey, UseStreaming = useStreaming, SendTemperature = sendTemperature }, ct);

    private async Task<ModelTestResult> TestConnectionAsync(string baseUrl, string model, string apiKey,
        AppSettings.TranslationSettingsSnapshot settings, CancellationToken ct)
    {
        baseUrl = (baseUrl ?? "").Trim(); model = (model ?? "").Trim(); apiKey = (apiKey ?? "").Trim();
        if (baseUrl.Length == 0 || model.Length == 0) return ModelTestResult.Fail("请先填写 API 地址和模型名称");
        if (settings.RequireApiKey && apiKey.Length == 0) return ModelTestResult.Fail("缺少 API Key；本地无鉴权服务可关闭 API Key 鉴权");
        var watch = Stopwatch.StartNew();
        try
        {
            var probe = new TranslationRequest("OK", null, "auto", "en", TranslationMode.Block,
                Quality: settings.Quality);
            using var request = CreateRequest(probe, baseUrl, model, apiKey, settings);
            await foreach (var _ in TranslationHttp.SendAsync(_httpClient, request, _timeout, ct).ConfigureAwait(false)) { }
            return ModelTestResult.Ok(watch.Elapsed);
        }
        catch (OperationCanceledException) { return ModelTestResult.Fail("测试已取消"); }
        catch (ArgumentException) { return ModelTestResult.Fail("API 地址无效，请使用不含用户名、密码和片段标识的 HTTP(S) 地址"); }
        catch (TranslationException ex) { return ModelTestResult.Fail(FriendlyError(ex)); }
    }

    private static string FriendlyError(TranslationException ex) => ex.ErrorCode switch
    {
        TranslationErrorCode.AuthFailed => "API Key 无效或无权限访问该模型（401/403）",
        TranslationErrorCode.Throttled => "被限流（429），请稍后再试",
        TranslationErrorCode.NetworkUnavailable => "无法连接服务，请检查 API 地址和网络",
        TranslationErrorCode.Timeout => "请求超时，请检查 API 地址是否可达",
        TranslationErrorCode.BadRequest => "请求参数不受支持，请检查模型名称及参数设置（400）",
        TranslationErrorCode.InvalidResponse => "响应异常，请检查模型能力与服务协议",
        _ => "翻译服务请求失败，请检查服务状态"
    };

    public static Uri BuildChatCompletionsUrl(string baseUrl) => ChatCompletionsEndpoint.Normalize(baseUrl);
}

/// <summary>模型连通性测试结果（设置窗口「测试连接」）。</summary>
public sealed record ModelTestResult(bool Success, string Message, TimeSpan? Latency = null)
{
    public static ModelTestResult Ok(TimeSpan latency) => new(true, string.Empty, latency);
    public static ModelTestResult Fail(string message) => new(false, message);
}

internal class OpenAiChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

internal class OpenAiChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<OpenAiChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }
}
