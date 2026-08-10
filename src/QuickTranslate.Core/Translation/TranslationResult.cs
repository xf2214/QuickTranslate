namespace QuickTranslate.Core.Translation;

public record TranslationResult(
    string NormalizedKey,
    string SourceText,
    string TargetText,
    string? TargetLanguage = null,
    bool FromCache = false,
    bool FromDictionary = false,
    bool NeedsOnline = false,
    TimeSpan? Latency = null,
    string? ErrorCode = null);
