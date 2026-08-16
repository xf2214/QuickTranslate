using System.Net.Http.Headers;
using System.Net.Sockets;
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
/// 保证开发/演示环境不报错；配置齐全后走真实 HTTP，优先 SSE 流式，自动回退非流式。
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

    private (string BaseUrl, string Model, string ApiKey) CurrentConfig
    {
        get
        {
            var s = _settings.Value;
            return ((s.CustomLlmBaseUrl ?? string.Empty).Trim(),
                (s.CustomLlmModel ?? string.Empty).Trim(),
                (s.ResolvedApiKey ?? string.Empty).Trim());
        }
    }

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest req,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (baseUrl, model, apiKey) = CurrentConfig;
        if (baseUrl.Length == 0 || model.Length == 0 || apiKey.Length == 0)
        {
            _logger.LogDebug("Custom LLM not fully configured (baseUrl/model/apiKey); serving mock stream");
            var mock = MockText(req);
            yield return new TranslationChunk(TextDelta: mock, IsFinal: true, FullTranslation: mock);
            yield break;
        }

        await foreach (var c in RealTranslateAsync(req, baseUrl, model, apiKey, ct))
        {
            yield return c;
        }
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

    internal static string BuildSystemPrompt(TranslationRequest req) =>
        "You are a professional translator. Translate the user's text into " +
        $"{LanguageName(req.TargetLanguage)}. " +
        (req.Mode == TranslationMode.Word
            ? "The input is a single word or short phrase: give its most common translation, optionally followed by a brief gloss in parentheses. "
            : "Preserve paragraph breaks. ") +
        "Output ONLY the translation. No explanations, no quotes, no extra formatting.";

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

    internal async IAsyncEnumerable<TranslationChunk> RealTranslateAsync(
        TranslationRequest req,
        string baseUrl,
        string model,
        string apiKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new OpenAiChatRequest
        {
            Model = model,
            Messages = new List<OpenAiChatMessage>
            {
                new() { Role = "system", Content = BuildSystemPrompt(req) },
                new() { Role = "user", Content = req.Text }
            },
            Stream = true,
            Temperature = 0.1
        };

        var uri = BuildChatCompletionsUrl(baseUrl);
        var json = JsonSerializer.Serialize(body);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await SendWithTimeoutAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TranslationException.Timeout();
        }
        catch (HttpRequestException hre) when (hre.InnerException is SocketException)
        {
            throw TranslationException.NetworkUnavailable(inner: hre);
        }
        catch (HttpRequestException hre)
        {
            throw new TranslationException(TranslationErrorCode.Unknown, hre.Message, hre);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var c in ConsumeSseAsync(response, ct))
            {
                yield return c;
            }
            yield break;
        }

        await foreach (var c in ConsumeJsonAsync(response, ct))
        {
            yield return c;
        }
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = "";
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch { }

            var shortBody = Truncate(errorBody, 300);
            var ex = TranslationException.FromHttpStatus((int)response.StatusCode, shortBody);
            response.Dispose();
            throw ex;
        }

        return response;
    }

    private static async IAsyncEnumerable<TranslationChunk> ConsumeSseAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var full = new StringBuilder();

            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line.Substring("data:".Length).Trim();
                if (payload.Length == 0 || payload == "[DONE]") continue;

                OpenAiChatResponse? frame = null;
                try
                {
                    frame = JsonSerializer.Deserialize<OpenAiChatResponse>(payload);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (frame?.Error?.Message is { } errMsg)
                {
                    throw TranslationException.InvalidResponse(errMsg);
                }

                var delta = frame?.Choices?.FirstOrDefault()?.Delta?.Content;
                if (!string.IsNullOrEmpty(delta))
                {
                    full.Append(delta);
                    yield return new TranslationChunk(TextDelta: delta, IsFinal: false);
                }
            }

            yield return new TranslationChunk(TextDelta: "", IsFinal: true, FullTranslation: full.ToString());
        }
        finally
        {
            response.Dispose();
        }
    }

    private static async IAsyncEnumerable<TranslationChunk> ConsumeJsonAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OpenAiChatResponse? frame;
            try
            {
                frame = JsonSerializer.Deserialize<OpenAiChatResponse>(raw);
            }
            catch (JsonException je)
            {
                throw TranslationException.InvalidResponse("非 JSON 响应: " + Truncate(raw, 200), je);
            }

            if (frame == null)
                throw TranslationException.InvalidResponse("响应为空");

            if (frame.Error?.Message is { } errMsg)
                throw TranslationException.InvalidResponse(errMsg);

            var text = frame.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrEmpty(text))
                throw TranslationException.InvalidResponse("choices[0].message.content 缺失");

            yield return new TranslationChunk(TextDelta: text, IsFinal: true, FullTranslation: text);
        }
        finally
        {
            response.Dispose();
        }
    }

    public static Uri BuildChatCompletionsUrl(string baseUrl)
    {
        // 用户可填带或不带尾斜杠、带或不带 /v1 的地址；统一拼出 {base}/chat/completions
        var baseUri = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        // 容错：省略 scheme 时补全——本地地址默认 http（Ollama/vLLM 常见），公网默认 https
        if (!baseUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var isLocal = baseUri.StartsWith("127.0.0.1", StringComparison.Ordinal) ||
                          baseUri.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                          baseUri.StartsWith("[::1]", StringComparison.Ordinal);
            baseUri = (isLocal ? "http://" : "https://") + baseUri;
        }
        return new Uri(baseUri + "/chat/completions", UriKind.Absolute);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
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
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.1;
}

internal class OpenAiDelta
{
    [JsonPropertyName("content")] public string? Content { get; set; }
}

internal class OpenAiChoice
{
    [JsonPropertyName("delta")] public OpenAiDelta? Delta { get; set; }
    [JsonPropertyName("message")] public OpenAiDelta? Message { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

internal class OpenAiChatResponse
{
    [JsonPropertyName("choices")] public List<OpenAiChoice>? Choices { get; set; }
    [JsonPropertyName("error")] public OpenAiError? Error { get; set; }
}

internal class OpenAiError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}
