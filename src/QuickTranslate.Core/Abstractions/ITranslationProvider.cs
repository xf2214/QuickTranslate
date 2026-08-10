using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Abstractions;

public interface ITranslationProvider
{
    string Name { get; }

    IAsyncEnumerable<TranslationChunk> TranslateAsync(TranslationRequest request, CancellationToken ct);
}
