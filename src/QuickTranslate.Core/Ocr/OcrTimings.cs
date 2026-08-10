namespace QuickTranslate.Core.Ocr;

public record OcrTimings(
    TimeSpan Preprocess,
    TimeSpan Detector,
    TimeSpan Classifier,
    TimeSpan Recognizer,
    TimeSpan Postprocess)
{
    public TimeSpan Total => Preprocess + Detector + Classifier + Recognizer + Postprocess;
}
