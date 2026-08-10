using QuickTranslate.Core.Options;

namespace QuickTranslate.Core.Translation;

public enum TranslationMode
{
    Word,
    Block
}

public record TranslationRequest(
    string Text,
    string? Context,
    string SourceLanguage,
    string TargetLanguage,
    TranslationMode Mode,
    Guid OperationId = default,
    TranslationQuality Quality = TranslationQuality.Fast);
