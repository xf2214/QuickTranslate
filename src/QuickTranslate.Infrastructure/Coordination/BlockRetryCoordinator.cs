using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;

namespace QuickTranslate.Infrastructure.Coordination;

public class BlockRetryCoordinator
{
    public IScreenCapture Capture { get; }
    public IOcrEngine Ocr { get; }
    public IBlockSelector Selector { get; }
    public IMonitorService Monitors { get; }
    public IOptions<AppSettings> Settings { get; }

    public BlockRetryCoordinator(
        IScreenCapture capture,
        IOcrEngine ocr,
        IBlockSelector selector,
        IMonitorService monitors,
        IOptions<AppSettings> settings)
    {
        Capture = capture;
        Ocr = ocr;
        Selector = selector;
        Monitors = monitors;
        Settings = settings;
    }

    public async Task<(OcrLayoutResult ocr, BlockSelectionResult block, int captures)> SelectBlockWithRetryAsync(
        PhysicalPoint anchor, MonitorId monitorId, uint dpiX, uint dpiY, CancellationToken ct)
    {
        int count = 0;
        var opts = SelectionOptions.Default;
        PhysicalSize size = new(1200, 720);
        using (var frame = await Capture.CaptureAroundAsync(anchor, size, ct))
        {
            count++;
            var ocr = await Ocr.RecognizeAsync(frame, ct);
            var block = Selector.SelectBlock(ocr, anchor, opts);
            if (block.NoBlockFound) return (ocr, block, count);
            var captureRegion = frame.Region;
            bool touchLeft = block.UnionBox.Left - captureRegion.Left < opts.BlockEdgeRetryThreshold;
            bool touchTop = block.UnionBox.Top - captureRegion.Top < opts.BlockEdgeRetryThreshold;
            bool touchRight = captureRegion.Right - block.UnionBox.Right < opts.BlockEdgeRetryThreshold;
            bool touchBottom = captureRegion.Bottom - block.UnionBox.Bottom < opts.BlockEdgeRetryThreshold;
            if (!touchLeft && !touchTop && !touchRight && !touchBottom) return (ocr, block, count);
            size = new PhysicalSize((int)Math.Round(size.Width * 1.8), (int)Math.Round(size.Height * 1.8));
        }
        using (var frame2 = await Capture.CaptureAroundAsync(anchor, size, ct))
        {
            count++;
            var ocr2 = await Ocr.RecognizeAsync(frame2, ct);
            var blk2 = Selector.SelectBlock(ocr2, anchor, opts);
            return (ocr2, blk2, count);
        }
    }
}
