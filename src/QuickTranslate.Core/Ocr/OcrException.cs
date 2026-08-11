namespace QuickTranslate.Core.Ocr;

public enum OcrErrorCode
{
    Unknown = 0,
    Cancelled = 1,
    ModelLoadFailed = 2,
    InferenceFailed = 3
}

public class OcrException : Exception
{
    public OcrErrorCode ErrorCode { get; }

    public OcrException(OcrErrorCode code, string msg, Exception? inner = null)
        : base(msg, inner)
    {
        ErrorCode = code;
    }

    public static OcrException Cancelled(string? msg = null, Exception? inner = null)
        => new(OcrErrorCode.Cancelled, msg ?? "OCR 被取消", inner);

    public static OcrException ModelLoadFailed(string? msg = null, Exception? inner = null)
        => new(OcrErrorCode.ModelLoadFailed, msg ?? "OCR 模型加载失败", inner);

    public static OcrException InferenceFailed(string? msg = null, Exception? inner = null)
        => new(OcrErrorCode.InferenceFailed, msg ?? "OCR 推理异常", inner);
}
