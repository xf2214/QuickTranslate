using System.Diagnostics;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;

namespace QuickTranslate.Infrastructure.Ocr;

public class MockOcrEngine : IOcrEngine
{
    private readonly ILogger<MockOcrEngine> _logger;

    public string EngineName => "MockOcr";
    public bool IsAvailable => false;

    public event EventHandler? SessionCreated
    {
        add { }
        remove { }
    }

    public MockOcrEngine(ILogger<MockOcrEngine> logger)
    {
        _logger = logger;
    }

    public Task WarmUpAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                throw OcrException.Cancelled(oce.Message, oce);
            }

            ct.ThrowIfCancellationRequested();

            var lines = new List<OcrLine>();
            var lineTexts = new[]
            {
                "The quick brown fox jumps over the lazy dog.",
                "PP-OCRv6 is an open-source OCR engine from PaddlePaddle.",
                "This project builds a desktop translation overlay around it.",
                "Global hotkeys Alt+1 selects a single English word.",
                "Alt+2 selects a paragraph block near the cursor."
            };

            int frameWidth = frame.Region.Width > 0 ? frame.Region.Width : 1920;
            int frameHeight = frame.Region.Height > 0 ? frame.Region.Height : 1080;
            int lineHeight = frameHeight / Math.Max(lineTexts.Length + 1, 1);

            for (int lineIdx = 0; lineIdx < lineTexts.Length; lineIdx++)
            {
                var lineText = lineTexts[lineIdx];
                var wordTokens = lineText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var words = new List<OcrWord>();

                int lineTop = lineIdx * lineHeight + 10;
                int lineBottom = lineTop + lineHeight - 20;
                int totalWidth = frameWidth - 40;
                int charWidth = wordTokens.Sum(w => w.Length);
                int currentX = 20;

                for (int wIdx = 0; wIdx < wordTokens.Length; wIdx++)
                {
                    var word = wordTokens[wIdx];
                    int wordWidth = (int)Math.Round((double)word.Length / charWidth * totalWidth);
                    if (wIdx == wordTokens.Length - 1)
                    {
                        wordWidth = (frameWidth - 20) - currentX;
                    }

                    var wordBox = new PhysicalRect(
                        X: currentX,
                        Y: lineTop,
                        Width: Math.Max(wordWidth, 5),
                        Height: lineBottom - lineTop);

                    words.Add(new OcrWord(wordBox, word, 0.95f, lineIdx));
                    currentX += wordWidth + 8;
                }

                var lineBox = new PhysicalRect(
                    X: 10,
                    Y: lineTop,
                    Width: frameWidth - 20,
                    Height: lineBottom - lineTop);

                lines.Add(new OcrLine(lineBox, words, lineText, 0f));
            }

            var elapsed = sw.Elapsed;
            var preprocess = TimeSpan.FromTicks(elapsed.Ticks / 5);
            var detector = TimeSpan.FromTicks(elapsed.Ticks / 5);
            var classifier = TimeSpan.FromTicks(elapsed.Ticks / 5);
            var recognizer = TimeSpan.FromTicks(elapsed.Ticks / 5);
            var postprocess = elapsed - preprocess - detector - classifier - recognizer;

            var timings = new OcrTimings(preprocess, detector, classifier, recognizer, postprocess);
            var result = new OcrLayoutResult(
                frame.Region,
                lines,
                timings,
                DateTimeOffset.Now,
                DpiX: 96,
                DpiY: 96,
                EngineName: EngineName,
                FromCache: false);

            _logger.LogInformation(
                "OCR finished: Engine={EngineName} Lines={LineCount} PreprocessMs={PreprocessMs} DetectorMs={DetectorMs} ClassifierMs={ClassifierMs} RecognizerMs={RecognizerMs} PostprocessMs={PostprocessMs}",
                EngineName,
                result.LineCount,
                timings.Preprocess.TotalMilliseconds,
                timings.Detector.TotalMilliseconds,
                timings.Classifier.TotalMilliseconds,
                timings.Recognizer.TotalMilliseconds,
                timings.Postprocess.TotalMilliseconds);

            return result;
        }
        catch (OperationCanceledException oce)
        {
            throw OcrException.Cancelled(oce.Message, oce);
        }
        catch (OcrException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw OcrException.InferenceFailed(ex.Message, ex);
        }
    }
}
