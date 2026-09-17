using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Abstractions;

public interface IOcrEngine
{
    string EngineName { get; }
    bool IsAvailable { get; }
    Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default, bool forceCpu = false);

    /// <summary>
    /// 带焦点带的识别：引擎可只识别与 focusBand 垂直相交的行（屏幕绝对坐标），
    /// 大幅减少无关行的识别耗时与块选择噪声。默认实现忽略焦点带，行为同全量识别。
    /// forceCpu=true 时强制走 CPU（单词链路恒走 CPU，不受面积/focusBand/DML启发式影响）。
    /// </summary>
    Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, PhysicalRect? focusBand, CancellationToken ct = default, bool forceCpu = false)
        => RecognizeAsync(frame, ct, forceCpu);

    Task WarmUpAsync(CancellationToken ct = default);
    event EventHandler? SessionCreated;
}
