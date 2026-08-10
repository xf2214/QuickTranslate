using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Services;

public class StubTranslationProvider : ITranslationProvider
{
    public async Task<TranslationResult> TranslateWordAsync(string word, string targetLang, CancellationToken ct = default)
    {
        await Task.Delay(50, ct);
        return new TranslationResult(
            NormalizedKey: word?.ToLowerInvariant() ?? string.Empty,
            SourceText: word ?? string.Empty,
            TargetText: "在线翻译接入 M5",
            TargetLanguage: targetLang,
            FromCache: false,
            FromDictionary: false,
            NeedsOnline: true,
            ErrorCode: null);
    }

    public async Task<TranslationResult> TranslateBlockAsync(string blockText, string targetLang, CancellationToken ct = default)
    {
        await Task.Delay(50, ct);
        return new TranslationResult(
            NormalizedKey: blockText?.ToLowerInvariant() ?? string.Empty,
            SourceText: blockText ?? string.Empty,
            TargetText: "在线翻译接入 M5",
            TargetLanguage: targetLang,
            FromCache: false,
            FromDictionary: false,
            NeedsOnline: true,
            ErrorCode: null);
    }
}
