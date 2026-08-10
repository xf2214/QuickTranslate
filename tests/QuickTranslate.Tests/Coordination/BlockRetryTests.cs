using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Infrastructure.Coordination;
using QuickTranslate.Tests.Coordination;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class FakeBlockSelector : IBlockSelector
{
    public Func<OcrLayoutResult, PhysicalPoint, SelectionOptions?, BlockSelectionResult>? SelectFunc { get; set; }

    public BlockSelectionResult SelectBlock(OcrLayoutResult ocr, PhysicalPoint anchor, SelectionOptions? opts = null)
    {
        if (SelectFunc != null) return SelectFunc(ocr, anchor, opts);
        return new BlockSelectionResult(
            BlockText: "block",
            UnionBox: new PhysicalRect(200, 200, 400, 200),
            SelectedLines: new[] { new OcrLine(new PhysicalRect(200, 200, 400, 200), Array.Empty<OcrWord>(), "block") },
            Kind: SelectionKind.Block,
            OperationId: Guid.NewGuid(),
            NoBlockFound: false);
    }
}

public class BlockRetryTests
{
    private static ScreenFrame CreateFrame(PhysicalRect region)
    {
        var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), PixelFormat.Format32bppArgb);
        return new ScreenFrame(bmp, region, MonitorId.Empty);
    }

    private static OcrLayoutResult CreateOcrResult(PhysicalRect region)
    {
        return new OcrLayoutResult(
            CaptureRegion: region,
            Lines: Array.Empty<OcrLine>(),
            Timings: new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            CaptureTime: DateTimeOffset.Now,
            DpiX: 96,
            DpiY: 96,
            EngineName: "FakeOcr");
    }

    [Fact]
    public async Task Case1_EdgeNotTouching_CaptureOnce()
    {
        var capture = new FakeScreenCapture();
        var ocr = new FakeOcrEngine();
        var selector = new FakeBlockSelector();
        var monitors = new FakeMonitorService();
        var settings = Options.Create(new AppSettings());

        var frameRegion = new PhysicalRect(0, 0, 1200, 720);
        selector.SelectFunc = (_, _, _) => new BlockSelectionResult(
            BlockText: "center block",
            UnionBox: new PhysicalRect(400, 260, 400, 200),
            SelectedLines: Array.Empty<OcrLine>(),
            Kind: SelectionKind.Block,
            OperationId: Guid.NewGuid(),
            NoBlockFound: false);

        var coord = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);

        var captureTask = coord.SelectBlockWithRetryAsync(
            new PhysicalPoint(600, 360), MonitorId.Empty, 96, 96, CancellationToken.None);

        capture.CaptureAroundTcs.SetResult(CreateFrame(frameRegion));
        ocr.RecognizeTcs.SetResult(CreateOcrResult(frameRegion));

        var (_, _, captures) = await captureTask;

        Assert.Equal(1, captures);
        Assert.Equal(1, capture.CaptureAroundCount);
    }

    [Fact]
    public async Task Case2_TouchLeftEdge_RetryOnce()
    {
        var capture = new FakeScreenCapture();
        var ocr = new FakeOcrEngine();
        var selector = new FakeBlockSelector();
        var monitors = new FakeMonitorService();
        var settings = Options.Create(new AppSettings());

        var frameRegion1 = new PhysicalRect(0, 0, 1200, 720);
        var frameRegion2 = new PhysicalRect(0, 0, 2160, 1296);
        int call = 0;
        selector.SelectFunc = (_, _, _) =>
        {
            call++;
            if (call == 1)
            {
                return new BlockSelectionResult(
                    BlockText: "edge block",
                    UnionBox: new PhysicalRect(10, 260, 400, 200),
                    SelectedLines: Array.Empty<OcrLine>(),
                    Kind: SelectionKind.Block,
                    OperationId: Guid.NewGuid(),
                    NoBlockFound: false);
            }
            return new BlockSelectionResult(
                BlockText: "expanded block",
                UnionBox: new PhysicalRect(500, 500, 400, 200),
                SelectedLines: Array.Empty<OcrLine>(),
                Kind: SelectionKind.Block,
                OperationId: Guid.NewGuid(),
                NoBlockFound: false);
        };

        var coord = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);

        var captureTask = coord.SelectBlockWithRetryAsync(
            new PhysicalPoint(600, 360), MonitorId.Empty, 96, 96, CancellationToken.None);

        capture.CaptureAroundTcs.SetResult(CreateFrame(frameRegion1));
        ocr.RecognizeTcs.SetResult(CreateOcrResult(frameRegion1));
        await Task.Delay(10);

        capture.CaptureAroundTcs = new TaskCompletionSource<ScreenFrame>();
        ocr.RecognizeTcs = new TaskCompletionSource<OcrLayoutResult>();
        capture.CaptureAroundTcs.SetResult(CreateFrame(frameRegion2));
        ocr.RecognizeTcs.SetResult(CreateOcrResult(frameRegion2));

        var (_, block, captures) = await captureTask;

        Assert.Equal(2, captures);
        Assert.Equal(2, capture.CaptureAroundCount);
        Assert.Equal("expanded block", block.BlockText);
    }
}
