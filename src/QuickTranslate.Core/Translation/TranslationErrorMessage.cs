namespace QuickTranslate.Core.Translation;

public static class TranslationErrorMessage
{
    public static string ForCode(TranslationErrorCode code) => code switch
    {
        TranslationErrorCode.Timeout => "翻译超时，请检查网络后重试",
        TranslationErrorCode.AuthFailed => "翻译服务授权失败，请检查 API Key 设置",
        TranslationErrorCode.NetworkUnavailable => "无网络连接，请检查网络设置",
        TranslationErrorCode.InvalidResponse => "翻译服务响应异常，请稍后再试",
        TranslationErrorCode.Throttled => "请求过于频繁，请稍后重试",
        TranslationErrorCode.BadRequest => "请求参数异常，请更换文本重试",
        _ => "翻译失败，请稍后再试"
    };
}
