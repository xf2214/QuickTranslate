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

public class QwenMtOptions
{
    public string BaseAddress { get; set; } = "https://dashscope.aliyuncs.com/";
    public string ApiKey { get; set; } = "";
    public Dictionary<TranslationQuality, string> ModelMap { get; set; } = new()
    {
        [TranslationQuality.Fast] = "qwen-mt-lite",
        [TranslationQuality.Balanced] = "qwen-mt-flash",
        [TranslationQuality.Best] = "qwen-mt-plus",
    };
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool UseStreaming { get; set; } = true;
    public bool MockMode { get; set; } = true;
    public int MockChunkCount { get; set; } = 4;
    public int MockChunkIntervalMs { get; set; } = 80;
}

internal class DashscopeOutput
{
    [JsonPropertyName("output_text")]
    public string? OutputText { get; set; }
}

internal class DashscopeUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

internal class DashscopeSseFrame
{
    [JsonPropertyName("output")]
    public DashscopeOutput? Output { get; set; }

    [JsonPropertyName("usage")]
    public DashscopeUsage? Usage { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal class DashscopeTranslateBody
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("input")]
    public DashscopeTranslateInput Input { get; set; } = new();

    [JsonPropertyName("parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();
}

internal class DashscopeTranslateInput
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("source_lang")]
    public string SourceLang { get; set; } = "";

    [JsonPropertyName("target_lang")]
    public string TargetLang { get; set; } = "";
}

public class QwenMtTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<QwenMtOptions> _opts;
    private readonly ILogger<QwenMtTranslationProvider> _logger;

    public string Name => "Qwen-MT";

    public QwenMtTranslationProvider(
        HttpClient httpClient,
        IOptions<QwenMtOptions> opts,
        ILogger<QwenMtTranslationProvider> logger)
    {
        _httpClient = httpClient;
        _opts = opts;
        _logger = logger;
    }

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest req,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_opts.Value.MockMode || string.IsNullOrWhiteSpace(_opts.Value.ApiKey))
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
        TranslationRequest req,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var opts = _opts.Value;
        using var timeoutCts = new CancellationTokenSource(opts.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        HttpResponseMessage? response;
        try
        {
            response = await SendTranslateRequestAsync(req, opts, linkedToken).ConfigureAwait(false);
        }
        catch (TranslationException)
        {
            throw;
        }
        catch (HttpRequestException hre)
        {
            if (hre.InnerException is SocketException
                || (hre.InnerException != null &&
                    hre.InnerException.GetType().Name.Contains("WinHttp", StringComparison.Ordinal)))
            {
                throw TranslationException.NetworkUnavailable(inner: hre);
            }

            throw TranslationException.InvalidResponse(hre.Message, hre);
        }
        catch (OperationCanceledException oce) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw TranslationException.Timeout(inner: oce);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException je)
        {
            throw TranslationException.InvalidResponse(inner: je);
        }
        catch (Exception ex)
        {
            throw new TranslationException(TranslationErrorCode.Unknown, ex.Message, ex);
        }

        await foreach (var c in ConsumeResponseStreamAsync(response, opts, linkedToken))
        {
            yield return c;
        }
    }

    private async Task<HttpResponseMessage> SendTranslateRequestAsync(
        TranslationRequest req,
        QwenMtOptions opts,
        CancellationToken linkedToken)
    {
        var modelName = opts.ModelMap.TryGetValue(req.Quality, out var m) ? m : opts.ModelMap[TranslationQuality.Fast];
        var uri = BuildTranslateUrl(opts);

        var body = new DashscopeTranslateBody
        {
            Model = modelName,
            Input = new DashscopeTranslateInput
            {
                Text = req.Text,
                SourceLang = req.SourceLanguage,
                TargetLang = req.TargetLanguage,
            },
            Parameters = new Dictionary<string, object>(),
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        request.Content = content;

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            linkedToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = "";
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(linkedToken).ConfigureAwait(false);
            }
            catch { }

            response.Dispose();
            throw TranslationException.FromHttpStatus((int)response.StatusCode, errorBody);
        }

        return response;
    }

    private static async IAsyncEnumerable<TranslationChunk> ConsumeResponseStreamAsync(
        HttpResponseMessage response,
        QwenMtOptions opts,
        [EnumeratorCancellation] CancellationToken linkedToken)
    {
        try
        {
            var fullSb = new StringBuilder();
            if (opts.UseStreaming)
            {
                await foreach (var frame in ParseSseStreamAsync(response, linkedToken))
                {
                    if (!string.IsNullOrEmpty(frame.Code) && frame.Code != "200")
                    {
                        var msg = frame.Message ?? "服务返回错误码";
                        if (frame.Code == "401" || frame.Code == "403")
                            throw TranslationException.AuthFailed(msg);
                        if (frame.Code == "429")
                            throw TranslationException.Throttled(msg);
                        if (frame.Code == "400")
                            throw TranslationException.BadRequest(msg);
                        throw TranslationException.InvalidResponse($"{frame.Code}: {msg}");
                    }

                    var delta = frame.Output?.OutputText;
                    if (!string.IsNullOrEmpty(delta))
                    {
                        fullSb.Append(delta);
                        yield return new TranslationChunk(TextDelta: delta, IsFinal: false);
                    }
                }
            }
            else
            {
                var raw = await response.Content.ReadAsStringAsync(linkedToken).ConfigureAwait(false);
                DashscopeSseFrame? frame;
                try
                {
                    frame = JsonSerializer.Deserialize<DashscopeSseFrame>(raw);
                }
                catch (JsonException je)
                {
                    throw TranslationException.InvalidResponse("非 JSON 响应", je);
                }

                if (frame == null)
                    throw TranslationException.InvalidResponse("响应为空");

                if (!string.IsNullOrEmpty(frame.Code) && frame.Code != "200")
                    throw TranslationException.BadRequest($"{frame.Code}: {frame.Message}");

                var delta = frame.Output?.OutputText;
                if (string.IsNullOrEmpty(delta))
                    throw TranslationException.InvalidResponse("output_text 缺失");

                fullSb.Append(delta);
                yield return new TranslationChunk(TextDelta: delta, IsFinal: false);
            }

            yield return new TranslationChunk(TextDelta: "", IsFinal: true, FullTranslation: fullSb.ToString());
        }
        finally
        {
            response.Dispose();
        }
    }

    private static Uri BuildTranslateUrl(QwenMtOptions opts)
    {
        var baseUri = opts.BaseAddress.EndsWith('/') ? opts.BaseAddress : opts.BaseAddress + "/";
        return new Uri(new Uri(baseUri), "api/v1/services/aigc/text-generation/generation");
    }

    private static async IAsyncEnumerable<DashscopeSseFrame> ParseSseStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var payload = line.Substring("data: ".Length).Trim();
                if (payload == "[DONE]" || payload.Length == 0)
                    continue;

                DashscopeSseFrame? frame = null;
                try
                {
                    frame = JsonSerializer.Deserialize<DashscopeSseFrame>(payload);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (frame != null)
                    yield return frame;
            }
        }
    }
}
