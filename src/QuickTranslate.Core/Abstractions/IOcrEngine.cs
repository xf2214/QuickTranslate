using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Abstractions;

public interface IOcrEngine
{
    string EngineName { get; }
    bool IsAvailable { get; }
    Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default);

    /// <summary>
    /// 带焦点带的识别：引擎可只识别与 focusBand 垂直相交的行（屏幕绝对坐标），
    /// 大幅减少无关行的识别耗时与块选择噪声。默认实现忽略焦点带，行为同全量识别。
    /// </summary>
    Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, PhysicalRect? focusBand, CancellationToken ct = default)
        => RecognizeAsync(frame, ct);

    Task WarmUpAsync(CancellationToken ct = default);
    event EventHandler? SessionCreated;
}
