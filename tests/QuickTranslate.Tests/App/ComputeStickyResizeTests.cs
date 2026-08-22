using QuickTranslate.App.Services;
using QuickTranslate.Core.Geometry;
using Xunit;

namespace QuickTranslate.Tests.App;

/// <summary>
/// WpfBlockPopupService.ComputeStickyResize 纯几何单测（无需 STA / Dispatcher）。
/// 验证自适应闭环的粘性不变式：同侧延伸、clamp、不翻边、幂等。
/// </summary>
public class ComputeStickyResizeTests
{
    private static readonly PhysicalRect WorkArea1920 = new(0, 0, 1920, 1080);

    private static (double wDip, double hDip) WorkDip(PhysicalRect work, uint dpiX, uint dpiY)
        => (work.Width * 96.0 / dpiX, work.Height * 96.0 / dpiY);

    [Fact]
    public void ComputeStickyResize_LastBelow_StillBelow_AndClampsAtBottom()
    {
        uint dpi = 96;
        var anchor = new PhysicalRect(800, 950, 120, 40);
        // 上次在锚点下方（首次 Below 布局的结果）
        var previous = new PhysicalRect(700, anchor.Bottom, 440, 80);
        var (wDip, hDip) = WorkDip(WorkArea1920, dpi, dpi);

        // 长译文使 preferred 高度远超下方剩余空间（90px），应粘住下方并 clamp 底边
        var longTranslation = new string('译', 500);

        var result = WpfBlockPopupService.ComputeStickyResize(
            anchor, WorkArea1920, "source preview", longTranslation, wDip, hDip, dpi, dpi, previous);

        // 必须仍在 WorkArea 内且未翻到锚点上方
        Assert.True(result.Y >= anchor.Bottom - 4, $"应粘住下方，得到 {result} 锚点 {anchor}");
        Assert.Equal(WorkArea1920.Bottom, result.Bottom);
        // 若翻到上方，Top 将为 anchor.Top - Height（约 650），粘性下方不应等于该值
        Assert.NotEqual(anchor.Top - result.Height, result.Top);
    }

    [Fact]
    public void ComputeStickyResize_DefaultPrevious_EqualsAutoPlace()
    {
        uint dpi = 96;
        var anchor = new PhysicalRect(740, 100, 440, 40);
        var (wDip, hDip) = WorkDip(WorkArea1920, dpi, dpi);
        var source = "Hello world";
        var translation = "你好世界";

        var sticky = WpfBlockPopupService.ComputeStickyResize(
            anchor, WorkArea1920, source, translation, wDip, hDip, dpi, dpi, default);

        // default 应等同 Auto 放置
        var (estW, estH) = PopupSizeEstimator.EstimateBlockPopupSize(source, translation, wDip, hDip);
        var preferred = new PhysicalSize(
            (int)Math.Round(estW * dpi / 96.0),
            (int)Math.Round(estH * dpi / 96.0));
        var expected = PopupPlacement.Place(anchor, WorkArea1920, preferred);

        Assert.Equal(expected, sticky);
    }

    [Fact]
    public void ComputeStickyResize_SameInput_ReturnsEqualToLastPlaced()
    {
        uint dpi = 96;
        var anchor = new PhysicalRect(740, 300, 200, 40);
        var (wDip, hDip) = WorkDip(WorkArea1920, dpi, dpi);
        var source = "source preview text";
        var translation = "短译文";

        // 先算一次得到基准矩形
        var baseline = WpfBlockPopupService.ComputeStickyResize(
            anchor, WorkArea1920, source, translation, wDip, hDip, dpi, dpi, default);

        // 输入无变化时，以 baseline 为 lastPlaced 再算一次应返回相等矩形（幂等）
        var again = WpfBlockPopupService.ComputeStickyResize(
            anchor, WorkArea1920, source, translation, wDip, hDip, dpi, dpi, baseline);

        Assert.Equal(baseline, again);
    }

    [Fact]
    public void ComputeStickyResize_HighDpi_RoundingConsistent()
    {
        uint dpi = 144; // 150% 缩放
        var anchor = new PhysicalRect(800, 900, 120, 40);
        var previous = new PhysicalRect(700, anchor.Bottom, 440, 120);
        var work = new PhysicalRect(0, 0, 2880, 1620); // 1920*1.5
        var (wDip, hDip) = WorkDip(work, dpi, dpi);
        var translation = new string('a', 200);

        var result = WpfBlockPopupService.ComputeStickyResize(
            anchor, work, "src", translation, wDip, hDip, dpi, dpi, previous);

        // 高 DPI 下仍应粘住下方且不超出工作区
        Assert.True(result.Y >= anchor.Bottom - 4);
        Assert.True(result.Bottom <= work.Bottom);
    }
}
