using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Infrastructure.Ocr;
using QuickTranslate.Tests.Infrastructure;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class OcrExceptionTests
{
    private static ScreenFrame CreateTestFrame(int width = 800, int height = 600)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var region = new PhysicalRect(0, 0, width, height);
        return new ScreenFrame(bmp, region, MonitorId.Empty);
    }

    [Fact]
    public void Cancelled_CodeCorrect()
    {
        var ex = OcrException.Cancelled();
        Assert.Equal(OcrErrorCode.Cancelled, ex.ErrorCode);
        Assert.Equal("OCR 被取消", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void ModelLoadFailed_CodeCorrect()
    {
        var ex = OcrException.ModelLoadFailed();
        Assert.Equal(OcrErrorCode.ModelLoadFailed, ex.ErrorCode);
        Assert.Equal("OCR 模型加载失败", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task CancellationWrapped()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        using var cts = new CancellationTokenSource(10);
        using var frame = CreateTestFrame();

        var ocrEx = await Assert.ThrowsAsync<OcrException>(() =>
            engine.RecognizeAsync(frame, cts.Token));

        Assert.Equal(OcrErrorCode.Cancelled, ocrEx.ErrorCode);
    }

    [Fact]
    public async Task InferenceFailed_Wrapped()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new ThrowingMockOcrEngine(logger);

        using var frame = CreateTestFrame();

        var ocrEx = await Assert.ThrowsAsync<OcrException>(() =>
            engine.RecognizeAsync(frame, CancellationToken.None));

        Assert.Equal(OcrErrorCode.InferenceFailed, ocrEx.ErrorCode);
        Assert.NotNull(ocrEx.InnerException);
        Assert.IsType<InvalidDataException>(ocrEx.InnerException);
    }

    private class ThrowingMockOcrEngine : IOcrEngine
    {
        private readonly ILogger _logger;

        public string EngineName => "ThrowingMockOcr";
        public bool IsAvailable => false;
        public event EventHandler? SessionCreated { add { } remove { } }

        public ThrowingMockOcrEngine(ILogger logger)
        {
            _logger = logger;
        }

        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default)
        {
            try
            {
                await Task.Yield();
                throw new InvalidDataException("corrupt image data");
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
}
