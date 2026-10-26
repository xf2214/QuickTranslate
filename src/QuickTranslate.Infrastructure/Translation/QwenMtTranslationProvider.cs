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

public class QwenMtOptions
{
    /// <summary>Qwen-MT OpenAI 兼容端点基地址，真实请求 POST {BaseAddress}/chat/completions。</summary>
    public string BaseAddress { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    public string ApiKey { get; set; } = "";
    public Dictionary<TranslationQuality, string> ModelMap { get; set; } = new()
    {
        [TranslationQuality.Fast] = "qwen-mt-lite",
        [TranslationQuality.Balanced] = "qwen-mt-flash",
        [TranslationQuality.Best] = "qwen-mt-plus",
    };
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    // 默认 false：配置了 API Key 就走真实 API；仅测试/演示显式打开。
    // 未配置 Key 时 provider 内部仍自动降级 Mock，不依赖此开关。
    public bool MockMode { get; set; } = false;
    public int MockChunkCount { get; set; } = 4;
    public int MockChunkIntervalMs { get; set; } = 80;
}

internal class QwenMtMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal class QwenMtTranslationOptions
{
    [JsonPropertyName("source_lang")]
    public string SourceLang { get; set; } = "auto";

    [JsonPropertyName("target_lang")]
    public string TargetLang { get; set; } = "";
}

/// <summary>
/// Qwen-MT 官方请求体：messages 有且仅有一条 user 消息（content 为待翻译文本），
/// 语种通过 translation_options 传递（非 OpenAI 标准参数，HTTP 调用时作顶层参数）。
/// </summary>
internal class QwenMtRequestBody
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<QwenMtMessage> Messages { get; set; } = new();

    [JsonPropertyName("translation_options")]
    public QwenMtTranslationOptions TranslationOptions { get; set; } = new();
}

public class QwenMtTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<QwenMtOptions> _opts;
    private readonly ILogger<QwenMtTranslationProvider> _logger;
    private readonly IOptions<AppSettings>? _appSettings;

    public string Name => "Qwen-MT";

    public QwenMtTranslationProvider(
        HttpClient httpClient,
        IOptions<QwenMtOptions> opts,
        ILogger<QwenMtTranslationProvider> logger,
        IOptions<AppSettings>? appSettings = null)
    {
        _httpClient = httpClient;
        _opts = opts;
        _logger = logger;
        _appSettings = appSettings;
    }

    // API Key 实时读取 AppSettings 单例（设置窗口保存后就地更新），与 CustomOpenAi 一致；
    // 未注入 AppSettings（如旧测试直接构造）时回退到 QwenMtOptions 首次解析快照。
    private string EffectiveApiKey => _appSettings != null
        ? (_appSettings.Value.ResolvedApiKey ?? string.Empty).Trim()
        : _opts.Value.ApiKey;

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest req,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_opts.Value.MockMode || string.IsNullOrWhiteSpace(EffectiveApiKey))
        {
            await foreach (var c in MockStream(req, ct))
            {
                yield return c;
            }
            yield break;
        }

        await foreach (var c in RealHttpTranslateAsync(req, ct))
        {
            yield return c;
        }
    }

    internal async IAsyncEnumerable<TranslationChunk> MockStream(
        TranslationRequest req,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var opts = _opts.Value;
        string fullText;

        if (string.IsNullOrWhiteSpace(req.Text))
        {
            fullText = "[示例]";
        }
        else
        {
            var isChinese = req.Text.Any(c => c >= 0x4e00 && c <= 0x9fff);
            if (req.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                fullText = isChinese ? req.Text : $"[译文]{req.Text}";
            }
            else
            {
                fullText = isChinese ? $"[Trans]{req.Text}" : req.Text;
            }
        }

        var chunks = SplitIntoChunks(fullText, opts.MockChunkCount);
        var sb = new StringBuilder();

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0 && opts.MockChunkIntervalMs > 0)
            {
                await Task.Delay(opts.MockChunkIntervalMs, ct).ConfigureAwait(false);
            }

            sb.Append(chunks[i]);
            bool isFinal = i == chunks.Count - 1;
            yield return new TranslationChunk(
                TextDelta: chunks[i],
                IsFinal: isFinal,
                FullTranslation: isFinal ? sb.ToString() : null);
        }
    }

    private static List<string> SplitIntoChunks(string text, int chunkCount)
    {
        if (chunkCount <= 1 || text.Length <= chunkCount)
        {
            return new List<string> { text };
        }

        var result = new List<string>(chunkCount);
        int baseSize = text.Length / chunkCount;
        int remainder = text.Length % chunkCount;
        int idx = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            int size = baseSize + (i < remainder ? 1 : 0);
            if (size <= 0) size = 1;
            if (idx + size > text.Length) size = text.Length - idx;
            if (size <= 0) break;
            result.Add(text.Substring(idx, size));
            idx += size;
        }

        if (idx < text.Length)
        {
            result[result.Count - 1] += text.Substring(idx);
        }

        return result;
    }

    internal async IAsyncEnumerable<TranslationChunk> RealHttpTranslateAsync(
        TranslationRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var opts = _opts.Value;
        var apiKey = EffectiveApiKey;
        var model = opts.ModelMap.TryGetValue(req.Quality, out var selected) ? selected : opts.ModelMap[TranslationQuality.Fast];
        var body = new QwenMtRequestBody
        {
            Model = model,
            Messages = new List<QwenMtMessage> { new() { Role = "user", Content = req.Text } },
            TranslationOptions = new QwenMtTranslationOptions
            {
                SourceLang = MapToApiLanguage(req.SourceLanguage),
                TargetLang = MapToApiLanguage(req.TargetLanguage)
            }
        };
        // Qwen-MT keeps its dedicated, non-streaming protocol.
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint.Normalize(opts.BaseAddress));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        await foreach (var chunk in TranslationHttp.SendAsync(_httpClient, request, opts.Timeout, ct).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>
    /// 把 BCP-47 语种代码映射为 Qwen-MT translation_options 要求的英文全称
    /// （如 zh-CN→Chinese、en→English）；auto/未知值原样透传。
    /// 不复用 CustomOpenAi.LanguageName：那里 zh→“Simplified Chinese”是为提示词设计，
    /// 与官方 translation_options 的语种名不一致。
    /// </summary>
    internal static string MapToApiLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "auto";
        var l = lang.Trim().ToLowerInvariant();
        if (l == "auto") return "auto";
        if (l.StartsWith("zh", StringComparison.Ordinal)) return "Chinese";
        if (l.StartsWith("en", StringComparison.Ordinal)) return "English";
        if (l.StartsWith("ja", StringComparison.Ordinal)) return "Japanese";
        if (l.StartsWith("ko", StringComparison.Ordinal)) return "Korean";
        if (l.StartsWith("fr", StringComparison.Ordinal)) return "French";
        if (l.StartsWith("de", StringComparison.Ordinal)) return "German";
        if (l.StartsWith("es", StringComparison.Ordinal)) return "Spanish";
        if (l.StartsWith("ru", StringComparison.Ordinal)) return "Russian";
        if (l.StartsWith("pt", StringComparison.Ordinal)) return "Portuguese";
        if (l.StartsWith("it", StringComparison.Ordinal)) return "Italian";
        if (l.StartsWith("th", StringComparison.Ordinal)) return "Thai";
        if (l.StartsWith("vi", StringComparison.Ordinal)) return "Vietnamese";
        if (l.StartsWith("ar", StringComparison.Ordinal)) return "Arabic";
        if (l.StartsWith("id", StringComparison.Ordinal)) return "Indonesian";
        return lang; // 已是英文全称（如 "Chinese"）或未知语种，原样传递
    }

}
