using System.Runtime.CompilerServices;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Translation;

/// <summary>Owns one timeout scope from request headers through the final response chunk.</summary>
internal static class TranslationHttp
{
    public static async IAsyncEnumerable<TranslationChunk> SendAsync(HttpClient client,
        HttpRequestMessage request, TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scope.CancelAfter(timeout);
        using var response = await GuardAsync(
            () => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.Token), ct, scope.Token)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        {
            // Deliberately do not read an untrusted error body. Services can echo keys or input.
            throw TranslationException.FromHttpStatus((int)response.StatusCode);
        }

        await using var chunks = OpenAiChatResponseReader.ReadAsync(response, scope.Token).GetAsyncEnumerator(scope.Token);
        while (await GuardAsync(() => chunks.MoveNextAsync().AsTask(), ct, scope.Token).ConfigureAwait(false))
            yield return chunks.Current;
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation, CancellationToken caller, CancellationToken scope)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) { throw TranslationException.Timeout(); }
        catch (HttpRequestException)
        {
            if (caller.IsCancellationRequested) throw new OperationCanceledException(caller);
            if (scope.IsCancellationRequested) throw TranslationException.Timeout();
            throw TranslationException.NetworkUnavailable();
        }
        catch (IOException)
        {
            if (caller.IsCancellationRequested) throw new OperationCanceledException(caller);
            if (scope.IsCancellationRequested) throw TranslationException.Timeout();
            throw TranslationException.NetworkUnavailable();
        }
    }
}
