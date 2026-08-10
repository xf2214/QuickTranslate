namespace QuickTranslate.Core.Translation;

public record TranslationChunk(
    string TextDelta,
    bool IsFinal = false,
    string? FullTranslation = null);
