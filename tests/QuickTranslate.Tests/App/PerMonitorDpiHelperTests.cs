using Xunit;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Tests.App;

public class PerMonitorDpiHelperTests
{
    [Fact]
    public void ToDip_96Dpi_OneToOne()
    {
        var box = new PhysicalRect(100, 200, 300, 400);
        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(box, 96, 96);
        Assert.Equal(100.0, l, 2);
        Assert.Equal(200.0, t, 2);
        Assert.Equal(300.0, w, 2);
        Assert.Equal(400.0, h, 2);
    }

    [Fact]
    public void ToDip_192Dpi_Scales_0_5()
    {
        var box = new PhysicalRect(100, 200, 300, 400);
        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(box, 192, 192);
        Assert.Equal(50.0, l, 2);
        Assert.Equal(100.0, t, 2);
        Assert.Equal(150.0, w, 2);
        Assert.Equal(200.0, h, 2);
    }

    [Fact]
    public void ToDip_144Dpi_ScalesBy2Thirds()
    {
        var box = new PhysicalRect(100, 200, 300, 400);
        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(box, 144, 144);
        double factor = 96.0 / 144.0;
        Assert.Equal(100.0 * factor, l, 2);
        Assert.Equal(200.0 * factor, t, 2);
        Assert.Equal(300.0 * factor, w, 2);
        Assert.Equal(400.0 * factor, h, 2);
    }

    [Fact]
    public void ToDip_WithSecondaryMonitor_ComputesRelativeOffsetCorrectly()
    {
        // 副屏物理边界 (1920, 0, 1920, 1080), 192 DPI (200%), 目标在 (1920, 200, 300, 150)
        var monitorBounds = new PhysicalRect(1920, 0, 1920, 1080);
        var box = new PhysicalRect(1920, 200, 300, 150);

        // 如果不按相对原点算：1920 * 96 / 192 = 960 (落入主屏范围)
        var (naiveL, naiveT, naiveW, naiveH) = PerMonitorDpiHelpers.ToDip(box, 192, 192);
        Assert.Equal(960.0, naiveL);

        // 按相对原点计算：DipLeft + (PhysicalX - Monitor.PhysicalLeft) * (96.0 / Monitor.DpiX)
        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(box, 192, 192, monitorBounds, monitorDipLeft: 1920, monitorDipTop: 0);
        Assert.Equal(1920.0, l); // 位于副屏起始位置，不会落入主屏
        Assert.Equal(100.0, t);
        Assert.Equal(150.0, w);
        Assert.Equal(75.0, h);
    }

    [Fact]
    public void ToWpfRect_WithMonitorInfo_ComputesRelativeOffsetCorrectly()
    {
        var monitorBounds = new PhysicalRect(1920, 0, 3840, 2160);
        var monitor = new MonitorInfo(
            new MonitorId(new IntPtr(3), @"\\.\DISPLAY3"),
            @"\\.\DISPLAY3",
            monitorBounds,
            monitorBounds,
            144,
            144,
            false);

        var box = new PhysicalRect(2064, 144, 288, 144);
        var rect = PerMonitorDpiHelpers.ToWpfRect(box, monitor);

        // 相对 X = 2064 - 1920 = 144px -> 144 * 96 / 144 = 96 DIP
        // DipX = 1920 + 96 = 2016
        Assert.Equal(2016.0, rect.Left, 2);
        Assert.Equal(144 * 96.0 / 144.0, rect.Top, 2);
        Assert.Equal(288 * 96.0 / 144.0, rect.Width, 2);
        Assert.Equal(144 * 96.0 / 144.0, rect.Height, 2);
    }

    [Fact]
    public void AreClose_Equal_True()
    {
        Assert.True(PerMonitorDpiHelpers.AreClose(96, 96));
    }

    [Fact]
    public void AreClose_OffBy2_True_OffBy3_False()
    {
        Assert.True(PerMonitorDpiHelpers.AreClose(96, 98));
        Assert.False(PerMonitorDpiHelpers.AreClose(96, 99));
    }
}
