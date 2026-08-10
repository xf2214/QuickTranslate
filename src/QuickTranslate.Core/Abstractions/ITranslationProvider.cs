using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Abstractions;

public interface ITranslationProvider
{
    Task<TranslationResult> TranslateWordAsync(string word, string targetLang, CancellationToken ct = default);

    Task<TranslationResult> TranslateBlockAsync(string blockText, string targetLang, CancellationToken ct = default);
}
