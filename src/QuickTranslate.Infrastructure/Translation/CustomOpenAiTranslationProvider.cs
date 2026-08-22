using System.Diagnostics;
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
            Temperature = TemperatureFor(req.Quality)
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
            catch (Exception ex)
            {
                // Best-effort read — keep fallback to empty body semantics, but log diagnostic.
                _logger.LogDebug(ex, "[CustomOpenAi.Send] Failed to read error body [ErrorCode=CUSTOM_ERROR_BODY_READ_FAIL] {ExType}: {Message}", ex.GetType().Name, ex.Message);
            }

            var shortBody = Truncate(errorBody, 300);
            var translationEx = TranslationException.FromHttpStatus((int)response.StatusCode, shortBody);
            response.Dispose();
            throw translationEx;
        }

        return response;
    }

    private async IAsyncEnumerable<TranslationChunk> ConsumeSseAsync(
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
                catch (JsonException ex)
                {
                    // Malformed SSE JSON frame — keep fallback semantics (skip frame, continue stream) but log diagnostic.
                    _logger.LogDebug(ex, "[CustomOpenAi.SSE] Skipping malformed JSON frame [ErrorCode=CUSTOM_SSE_JSON_SKIP] {ExType}: {Message}", ex.GetType().Name, ex.Message);
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

            // SSE 正常结束但无任何内容：视为响应异常，避免空译文被上层缓存/展示
            if (full.Length == 0)
                throw TranslationException.InvalidResponse("模型返回内容为空");

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

    // ===== 模型连通性测试（设置窗口「测试连接」按钮）=====

    private const string TestProbeText = "Please reply with exactly: OK";

    /// <summary>
    /// 用给定的 BaseUrl/Model/ApiKey 发一次最小化非流式请求，验证模型可用性并测量延迟。
    /// 不依赖 AppSettings 当前值，便于测试窗口里尚未保存的配置。
    /// </summary>
    public async Task<ModelTestResult> TestConnectionAsync(
        string baseUrl, string model, string apiKey, CancellationToken ct = default)
    {
        baseUrl = (baseUrl ?? string.Empty).Trim();
        model = (model ?? string.Empty).Trim();
        apiKey = (apiKey ?? string.Empty).Trim();

        if (baseUrl.Length == 0 || model.Length == 0)
            return ModelTestResult.Fail("请先填写 API 地址和模型名称");
        if (apiKey.Length == 0)
            return ModelTestResult.Fail("缺少 API Key（本地 Ollama 可填任意非空值）");

        var body = new OpenAiChatRequest
        {
            Model = model,
            Messages = new List<OpenAiChatMessage>
            {
                new() { Role = "user", Content = TestProbeText }
            },
            Stream = false,
            Temperature = 0,
            MaxTokens = 16
        };

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUrl(baseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            response = await SendWithTimeoutAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelTestResult.Fail("请求超时（" + (int)_timeout.TotalSeconds + "s），请检查 API 地址是否可达");
        }
        catch (OperationCanceledException)
        {
            return ModelTestResult.Fail("测试已取消");
        }
        catch (TranslationException tex)
        {
            return ModelTestResult.Fail(FriendlyError(tex));
        }
        catch (HttpRequestException hre) when (hre.InnerException is SocketException)
        {
            return ModelTestResult.Fail("无法连接服务，请检查 API 地址：" + hre.Message);
        }
        catch (HttpRequestException hre)
        {
            return ModelTestResult.Fail("请求失败：" + hre.Message);
        }

        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();

            OpenAiChatResponse? frame;
            try
            {
                frame = JsonSerializer.Deserialize<OpenAiChatResponse>(raw);
            }
            catch (JsonException)
            {
                return ModelTestResult.Fail("响应不是有效 JSON：" + Truncate(raw, 120));
            }

            if (frame?.Error?.Message is { } errMsg)
                return ModelTestResult.Fail("服务端错误：" + Truncate(errMsg, 150));

            var text = frame?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(text))
                return ModelTestResult.Fail("模型返回内容为空");

            _logger.LogInformation("Model test succeeded: {Model} in {Ms} ms", model, sw.ElapsedMilliseconds);
            return ModelTestResult.Ok(sw.Elapsed);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static string FriendlyError(TranslationException ex) => ex.ErrorCode switch
    {
        TranslationErrorCode.AuthFailed => "API Key 无效或无权限访问该模型（401/403）",
        TranslationErrorCode.Throttled => "被限流（429），请稍后再试",
        TranslationErrorCode.NetworkUnavailable => "无法连接服务，请检查 API 地址和网络",
        TranslationErrorCode.Timeout => "请求超时，请检查 API 地址是否可达",
        TranslationErrorCode.InvalidResponse => "响应异常：" + ex.Message,
        _ => ex.Message
    };

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
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.1;
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
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
