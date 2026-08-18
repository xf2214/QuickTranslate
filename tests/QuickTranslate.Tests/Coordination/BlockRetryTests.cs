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
        // Block 模式识别必须携带以光标为中心的焦点带（半高 280px）
        var band = Assert.Single(ocr.FocusBands);
        Assert.NotNull(band);
        Assert.Equal(360 - 280, band!.Value.Top);
        Assert.Equal(360 + 280, band.Value.Bottom);
    }

    [Fact]
    public async Task Case2b_TouchBandEdge_ExpandsBandOnSameFrame_NoRecapture()
    {
        // 块触焦点带边缘 → 对同一帧加宽焦点带再识别，不重抓截图
        var capture = new FakeScreenCapture();
        var ocr = new FakeOcrEngine();
        var selector = new FakeBlockSelector();
        var monitors = new FakeMonitorService();
        var settings = Options.Create(new AppSettings());

        var frameRegion1 = new PhysicalRect(0, 0, 1200, 720);
        int call = 0;
        selector.SelectFunc = (_, _, _) =>
        {
            call++;
            if (call == 1)
            {
                // union 底边 635 距初始焦点带底边 640 仅 5px < 阈值 20 → 触带；
                // 扩展后（半高 448，底边 808）不再触带也不触截图边缘 → 直接返回
                return new BlockSelectionResult(
                    BlockText: "band edge block",
                    UnionBox: new PhysicalRect(400, 400, 400, 235),
                    SelectedLines: new[] { new OcrLine(new PhysicalRect(400, 330, 400, 60), Array.Empty<OcrWord>(), "anchor line") },
                    Kind: SelectionKind.Block,
                    OperationId: Guid.NewGuid(),
                    NoBlockFound: false);
            }
            return new BlockSelectionResult(
                BlockText: "expanded block",
                UnionBox: new PhysicalRect(400, 400, 400, 300),
                SelectedLines: new[] { new OcrLine(new PhysicalRect(400, 330, 400, 60), Array.Empty<OcrWord>(), "anchor line") },
                Kind: SelectionKind.Block,
                OperationId: Guid.NewGuid(),
                NoBlockFound: false);
        };

        var coord = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);

        // 同帧两次识别的预置结果（队列避免 TCS 替换竞态）
        ocr.QueuedResults.Enqueue(CreateOcrResult(frameRegion1));
        ocr.QueuedResults.Enqueue(CreateOcrResult(frameRegion1));

        var captureTask = coord.SelectBlockWithRetryAsync(
            new PhysicalPoint(600, 360), MonitorId.Empty, 96, 96, CancellationToken.None);

        capture.CaptureAroundTcs.SetResult(CreateFrame(frameRegion1));

        var (_, block, captures) = await captureTask;

        Assert.Equal(1, captures);                      // 不重抓截图
        Assert.Equal(1, capture.CaptureAroundCount);
        Assert.Equal(2, ocr.RecognizeCount);            // 同帧再识别一次
        Assert.Equal("expanded block", block.BlockText);
        // 焦点带加宽 ×1.6（半高 280 → 448）
        Assert.Equal(2, ocr.FocusBands.Count);
        Assert.Equal(2 * 448, ocr.FocusBands[1]!.Value.Height);
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
                    UnionBox: new PhysicalRect(10, 260, 600, 200),
                    // 锚点行本身被左边缘截断（Left=10 < 阈值 20）→ 需要重抓
                    SelectedLines: new[] { new OcrLine(new PhysicalRect(10, 330, 600, 60), Array.Empty<OcrWord>(), "edge line") },
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

    [Fact]
    public async Task Case3_FarLineTouchesEdge_AnchorLineNotClipped_NoRetry()
    {
        // 无关宽行（UI 栏）触边但锚点行完整 → 不值得整块双倍 OCR，不重抓
        var capture = new FakeScreenCapture();
        var ocr = new FakeOcrEngine();
        var selector = new FakeBlockSelector();
        var monitors = new FakeMonitorService();
        var settings = Options.Create(new AppSettings());

        var frameRegion = new PhysicalRect(0, 0, 1200, 720);
        selector.SelectFunc = (_, _, _) => new BlockSelectionResult(
            BlockText: "block with wide bar",
            // union 触右边缘（来自远处的全宽行）
            UnionBox: new PhysicalRect(300, 260, 895, 220),
            SelectedLines: new[]
            {
                new OcrLine(new PhysicalRect(300, 330, 400, 60), Array.Empty<OcrWord>(), "anchor line"),
                new OcrLine(new PhysicalRect(300, 400, 895, 60), Array.Empty<OcrWord>(), "wide bar line")
            },
            Kind: SelectionKind.Block,
            OperationId: Guid.NewGuid(),
            NoBlockFound: false);

        var coord = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);

        var captureTask = coord.SelectBlockWithRetryAsync(
            new PhysicalPoint(500, 360), MonitorId.Empty, 96, 96, CancellationToken.None);

        capture.CaptureAroundTcs.SetResult(CreateFrame(frameRegion));
        ocr.RecognizeTcs.SetResult(CreateOcrResult(frameRegion));

        var (_, _, captures) = await captureTask;

        Assert.Equal(1, captures);
        Assert.Equal(1, capture.CaptureAroundCount);
    }

    [Fact]
    public async Task Case4_TouchTopEdge_ExpandThenRecapture()
    {
        // 触带 → 同帧扩展识别；扩展后块仍触截图上下边 → 再换更大截图重抓
        var capture = new FakeScreenCapture();
        var ocr = new FakeOcrEngine();
        var selector = new FakeBlockSelector();
        var monitors = new FakeMonitorService();
        var settings = Options.Create(new AppSettings());

        var frameRegion1 = new PhysicalRect(0, 0, 1200, 720);
        int call = 0;
        selector.SelectFunc = (_, _, _) =>
        {
            call++;
            if (call == 1)
            {
                // 触初始焦点带顶边（union top 5 < 带顶 80 + 20）→ 同帧扩展
                return new BlockSelectionResult(
                    BlockText: "top block",
                    UnionBox: new PhysicalRect(400, 5, 400, 200),
                    SelectedLines: new[] { new OcrLine(new PhysicalRect(400, 330, 400, 60), Array.Empty<OcrWord>(), "anchor line") },
                    Kind: SelectionKind.Block,
                    OperationId: Guid.NewGuid(),
                    NoBlockFound: false);
            }
            if (call == 2)
            {
                // 扩展后不再触带，但触截图顶边 → 换更大截图重抓
                return new BlockSelectionResult(
                    BlockText: "still clipped block",
                    UnionBox: new PhysicalRect(400, 5, 400, 300),
                    SelectedLines: new[] { new OcrLine(new PhysicalRect(400, 330, 400, 60), Array.Empty<OcrWord>(), "anchor line") },
                    Kind: SelectionKind.Block,
                    OperationId: Guid.NewGuid(),
                    NoBlockFound: false);
            }
            return new BlockSelectionResult(
                BlockText: "expanded block",
                UnionBox: new PhysicalRect(400, 300, 400, 300),
                SelectedLines: Array.Empty<OcrLine>(),
                Kind: SelectionKind.Block,
                OperationId: Guid.NewGuid(),
                NoBlockFound: false);
        };

        var coord = new BlockRetryCoordinator(capture, ocr, selector, monitors, settings);

        // 三次识别的预置结果：同帧两次（初始带/扩展带） + 重抓帧一次
        ocr.QueuedResults.Enqueue(CreateOcrResult(frameRegion1));
        ocr.QueuedResults.Enqueue(CreateOcrResult(frameRegion1));
        ocr.QueuedResults.Enqueue(CreateOcrResult(new PhysicalRect(0, 0, 1680, 1008)));

        var captureTask = coord.SelectBlockWithRetryAsync(
            new PhysicalPoint(600, 360), MonitorId.Empty, 96, 96, CancellationToken.None);

        capture.CaptureAroundTcs.SetResult(CreateFrame(frameRegion1));

        var (_, block, captures) = await captureTask;

        Assert.Equal(2, captures);              // 重抓一次
        Assert.Equal(3, ocr.RecognizeCount);    // 初始 + 同帧扩展 + 重抓帧
        Assert.Equal(3, ocr.FocusBands.Count);
        Assert.Equal("expanded block", block.BlockText);
    }
}
