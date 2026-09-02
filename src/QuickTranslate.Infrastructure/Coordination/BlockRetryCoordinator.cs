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
        // 96-DPI 基准首捕尺寸与焦点带：高 DPI 屏（150%/200% 缩放）上按监视器 DPI 缩放，
        // 保证首捕/焦点带的逻辑大小与 96-DPI 屏一致（96 DPI 下缩放系数为 1，行为不变）
        PhysicalSize size = new(DpiScale.Px(InitialCaptureWidth, dpiX), DpiScale.Px(InitialCaptureHeight, dpiY));
        // 焦点带：以光标为中心的垂直带，只识别带内的行。
        // 截图里的工具栏/侧栏/远处代码等无关行不再消耗 rec 耗时（每行 ~100ms），
        // 也不进块选择候选，同时改善“句子不准”与“严重卡顿”。
        int bandHalf = DpiScale.Px(FocusBandHalfHeight, dpiY);
        int edgeRetry = DpiScale.Px(opts.BlockEdgeRetryThreshold, dpiY);

        using (var frame = await Capture.CaptureAroundAsync(anchor, size, ct).ConfigureAwait(false))
        {
            count++;
            var band = MakeBand(anchor, bandHalf);
            var ocr = await Ocr.RecognizeAsync(frame, band, ct).ConfigureAwait(false);
            var block = Selector.SelectBlock(ocr, anchor, opts);

            // 触带扩展：块生长到焦点带边缘说明带外同一截图里还有未识别的行。
            // 直接加宽焦点带对原帧再识别（无需重抓截图，省去截图+det 开销），
            // 直到块不再触带、带已覆盖整帧或达到扩展上限。
            for (int expand = 0; expand < MaxBandExpansions && !block.NoBlockFound; expand++)
            {
                bool touchBandTop = block.UnionBox.Top - band.Top < edgeRetry;
                bool touchBandBottom = band.Bottom - block.UnionBox.Bottom < edgeRetry;
                if (!touchBandTop && !touchBandBottom) break;
                if (band.Top <= frame.Region.Top && band.Bottom >= frame.Region.Bottom)
                    break; // 当前带已覆盖整帧，再扩无意义

                int newHalf = (int)Math.Round(bandHalf * BandExpansionFactor);
                var newBand = MakeBand(anchor, newHalf);

                bandHalf = newHalf;
                band = newBand;
                ocr = await Ocr.RecognizeAsync(frame, band, ct).ConfigureAwait(false);
                block = Selector.SelectBlock(ocr, anchor, opts);
            }

            if (block.NoBlockFound) return (ocr, block, count);

            var captureRegion = frame.Region;
            bool touchTop = block.UnionBox.Top - captureRegion.Top < edgeRetry;
            bool touchBottom = captureRegion.Bottom - block.UnionBox.Bottom < edgeRetry;
            // 左右触边只在锚点行本身被截断时才值得重抓：
            // 无关宽行（UI 栏等）触边不影响目标句子的完整性，避免整块双倍 OCR 加重卡顿。
            bool anchorClippedHorizontally = AnchorLineTouchesHorizontalEdge(block, anchor, captureRegion, edgeRetry);
            if (!touchTop && !touchBottom && !anchorClippedHorizontally) return (ocr, block, count);
            size = new PhysicalSize((int)Math.Round(size.Width * 1.4), (int)Math.Round(size.Height * 1.4));
        }

        // 截图边缘触边 → 换更大的截图区域重抓（保留已扩展的焦点带）
        using (var frame2 = await Capture.CaptureAroundAsync(anchor, size, ct).ConfigureAwait(false))
        {
            count++;
            var band2 = MakeBand(anchor, bandHalf);
            var ocr2 = await Ocr.RecognizeAsync(frame2, band2, ct).ConfigureAwait(false);
            var blk2 = Selector.SelectBlock(ocr2, anchor, opts);
            return (ocr2, blk2, count);
        }
    }

    // 96-DPI 基准首捕尺寸（物理像素），运行时经 DpiScale 按监视器 DPI 缩放
    private const int InitialCaptureWidth = 1200;
    private const int InitialCaptureHeight = 720;
    // 96-DPI 基准焦点带初始半高 280px（带宽 560 ≈ 720p 截图的 78%）：句子/常见段落一次完成，
    // 更长的段落通过触带扩展（同帧再识别，×1.6）补齐。
    private const int FocusBandHalfHeight = 280;
    private const int MaxBandExpansions = 2;
    private const double BandExpansionFactor = 1.6;

    private static PhysicalRect MakeBand(PhysicalPoint anchor, int halfHeight)
    {
        // 引擎只用 Top/Bottom 做垂直过滤，X/宽任意
        return new PhysicalRect(anchor.X - 1, anchor.Y - halfHeight, 2, halfHeight * 2);
    }

    /// <summary>锚点行（包含光标的行，无则取最近行）是否被截图左右边缘截断。</summary>
    private static bool AnchorLineTouchesHorizontalEdge(
        BlockSelectionResult block, PhysicalPoint anchor, PhysicalRect region, int threshold)
    {
        OcrLine? anchorLine = null;
        foreach (var line in block.SelectedLines)
        {
            if (line.Box.Contains(anchor))
            {
                anchorLine = line;
                break;
            }
        }
        if (anchorLine == null)
        {
            double best = double.MaxValue;
            foreach (var line in block.SelectedLines)
            {
                double dist = DistanceToRect(anchor, line.Box);
                if (dist < best)
                {
                    best = dist;
                    anchorLine = line;
                }
            }
        }
        if (anchorLine == null) return false;

        bool touchLeft = anchorLine.Box.Left - region.Left < threshold;
        bool touchRight = region.Right - anchorLine.Box.Right < threshold;
        return touchLeft || touchRight;
    }

    private static double DistanceToRect(PhysicalPoint p, PhysicalRect box)
    {
        int dx = 0;
        if (p.X < box.Left) dx = box.Left - p.X;
        else if (p.X >= box.Right) dx = p.X - (box.Right - 1);

        int dy = 0;
        if (p.Y < box.Top) dy = box.Top - p.Y;
        else if (p.Y >= box.Bottom) dy = p.Y - (box.Bottom - 1);

        return Math.Sqrt(dx * dx + dy * dy);
    }
}
