using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Translation;

/// <summary>Bounded text-only chat-completions decoder. Untrusted payloads never enter errors.</summary>
internal static class OpenAiChatResponseReader
{
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private const int MaxEventChars = 256 * 1024;
    private const int MaxTranslationChars = 4 * 1024 * 1024;
    private readonly record struct Frame(string? Content, string? FinishReason);

    public static async IAsyncEnumerable<TranslationChunk> ReadAsync(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var full = new StringBuilder();
            await foreach (var payload in ReadEventsAsync(response, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (payload.Trim() == "[DONE]")
                {
                    yield return Final(full.ToString());
                    yield break;
                }
                var frame = Parse(payload, streaming: true);
                ValidateFinish(frame.FinishReason);
                if (!string.IsNullOrEmpty(frame.Content))
                {
                    if (full.Length + frame.Content.Length > MaxTranslationChars) throw Invalid();
                    full.Append(frame.Content);
                    yield return new TranslationChunk(frame.Content, false);
                }
                if (frame.FinishReason != null)
                {
                    yield return Final(full.ToString());
                    yield break;
                }
            }
            throw TranslationException.InvalidResponse("翻译流未完整结束");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaxJsonBytes) throw Invalid();
            buffer.Write(block, 0, read);
        }
        var json = Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)), streaming: false);
        ValidateFinish(json.FinishReason);
        var final = Final(json.Content);
        yield return final with { TextDelta = final.FullTranslation! };
    }

    private static TranslationChunk Final(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw TranslationException.InvalidResponse("模型返回内容为空");
        return new TranslationChunk("", true, text);
    }

    private static Frame Parse(string payload, bool streaming)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                throw TranslationException.InvalidResponse("翻译服务返回错误");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw Invalid();
            if (choices.GetArrayLength() == 0)
            {
                if (streaming) return default; // Usage-only frames have no choices.
                throw Invalid();
            }
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object) throw Invalid();
            string? finish = null;
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                if (reason.ValueKind != JsonValueKind.String) throw Invalid();
                finish = reason.GetString();
            }
            string? text = null;
            if (choice.TryGetProperty(streaming ? "delta" : "message", out var message) && message.ValueKind != JsonValueKind.Null)
            {
                if (message.ValueKind != JsonValueKind.Object) throw Invalid();
                if (message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    if (content.ValueKind != JsonValueKind.String) throw Invalid();
                    text = content.GetString();
                }
            }
            else if (streaming && finish is null)
            {
                // A nonterminal choice must carry a delta, even if it is only an empty/role object.
                throw Invalid();
            }
            return new(text, finish);
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void ValidateFinish(string? reason)
    {
        if (reason is not null && reason != "stop")
            throw TranslationException.InvalidResponse("模型未返回完整译文，请检查输出限制或模型能力");
    }

    private static async IAsyncEnumerable<string> ReadEventsAsync(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var line = new StringBuilder();
        var data = new StringBuilder();
        var chars = new char[1024];
        bool previousCr = false;
        int read;
        while ((read = await reader.ReadAsync(chars.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                var ch = chars[i];
                if (previousCr && ch == '\n') { previousCr = false; continue; }
                previousCr = ch == '\r';
                if (ch is not ('\r' or '\n'))
                {
                    if (line.Length >= MaxEventChars) throw Invalid();
                    line.Append(ch);
                    continue;
                }
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        yield return data.ToString(0, data.Length - 1);
                        data.Clear();
                    }
                }
                else
                {
                    var value = line.ToString();
                    var colon = value.IndexOf(':', StringComparison.Ordinal);
                    var field = colon < 0 ? value : value[..colon];
                    if (field == "data")
                    {
                        var content = colon < 0 ? "" : value[(colon + 1)..];
                        if (content.StartsWith(' ')) content = content[1..];
                        if (data.Length + content.Length + 1 > MaxEventChars) throw Invalid();
                        data.Append(content).Append('\n');
                    }
                    line.Clear();
                }
            }
        }
        // An EOF cannot dispatch an unfinished event or certify completion.
        if (line.Length != 0 || data.Length != 0) throw Invalid();
    }

    private static TranslationException Invalid() => TranslationException.InvalidResponse("翻译服务响应格式无效或超出大小限制");
}
