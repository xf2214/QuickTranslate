namespace QuickTranslate.Core.Translation;

public enum TranslationErrorCode
{
    Unknown = 0,
    Timeout = 1,
    AuthFailed = 2,
    NetworkUnavailable = 3,
    InvalidResponse = 4,
    Throttled = 5,
    BadRequest = 6
}
