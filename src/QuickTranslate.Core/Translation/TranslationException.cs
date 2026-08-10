namespace QuickTranslate.Core.Translation;

public class TranslationException : Exception
{
    public TranslationErrorCode ErrorCode { get; }

    public TranslationException(TranslationErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = code;
    }

    public static TranslationException Timeout(string? msg = null, Exception? inner = null)
        => new(TranslationErrorCode.Timeout, msg ?? "翻译请求超时", inner);

    public static TranslationException AuthFailed(string? msg = null, Exception? inner = null)
        => new(TranslationErrorCode.AuthFailed, msg ?? "API Key 无效或已过期", inner);

    public static TranslationException NetworkUnavailable(string? msg = null, Exception? inner = null)
        => new(TranslationErrorCode.NetworkUnavailable, msg ?? "网络连接不可用", inner);

    public static TranslationException InvalidResponse(string? msg = null, Exception? inner = null)
        => new(TranslationErrorCode.InvalidResponse, msg ?? "翻译服务响应异常", inner);

    public static TranslationException Throttled(string? msg = null, Exception? inner = null)
        => new(TranslationErrorCode.Throttled, msg ?? "请求过于频繁，请稍后重试", inner);

    public static TranslationException BadRequest(string? msg = null, Exception? inner = null)
        => new(TranslationErrorCode.BadRequest, msg ?? "请求参数不正确", inner);

    public static TranslationException FromHttpStatus(int statusCode, string? body = null, Exception? inner = null)
        => statusCode switch
        {
            401 or 403 => AuthFailed(body ?? "Unauthorized", inner),
            429 => Throttled(body ?? "Too many requests", inner),
            400 => BadRequest(body ?? "Bad request", inner),
            >= 500 => new TranslationException(TranslationErrorCode.Unknown, $"服务端错误 ({statusCode})", inner),
            _ => new TranslationException(TranslationErrorCode.Unknown, $"HTTP {statusCode}", inner)
        };
}
