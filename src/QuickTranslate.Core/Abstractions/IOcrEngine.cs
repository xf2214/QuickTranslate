using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Core.Abstractions;

public interface IOcrEngine
{
    string EngineName { get; }
    bool IsAvailable { get; }
    Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default);
    Task WarmUpAsync(CancellationToken ct = default);
    event EventHandler? SessionCreated;
}
