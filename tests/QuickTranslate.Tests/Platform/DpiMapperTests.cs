using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class DpiMapperTests
{
    private readonly DpiMapper _mapper = new();

    [Theory]
    [InlineData(96, 96)]
    [InlineData(120, 120)]
    [InlineData(144, 144)]
    [InlineData(192, 192)]
    public void PhysicalPoint_ToDip_ToPhysical_Roundtrip(uint dpiX, uint dpiY)
    {
        var original = new PhysicalPoint(100, 200);
        var dip = _mapper.ToDip(original, dpiX, dpiY);
        var back = _mapper.ToPhysical(dip, dpiX, dpiY);

        Assert.Equal(original, back);
    }

    [Theory]
    [InlineData(96, 96)]
    [InlineData(120, 120)]
    [InlineData(144, 144)]
    [InlineData(192, 192)]
    public void PhysicalRect_ToDip_ToPhysical_Roundtrip(uint dpiX, uint dpiY)
    {
        var original = new PhysicalRect(10, 20, 300, 400);
        var dip = _mapper.ToDip(original, dpiX, dpiY);
        var back = _mapper.ToPhysical(dip, dpiX, dpiY);

        Assert.Equal(original, back);
    }

    [Theory]
    [InlineData(96, 96)]
    [InlineData(120, 120)]
    [InlineData(144, 144)]
    [InlineData(192, 192)]
    public void DipPoint_ToPhysical_ToDip_Roundtrip(uint dpiX, uint dpiY)
    {
        var original = new DipPoint(80.0, 160.0);
        var physical = _mapper.ToPhysical(original, dpiX, dpiY);
        var back = _mapper.ToDip(physical, dpiX, dpiY);

        Assert.Equal(original.X, back.X, 1e-9);
        Assert.Equal(original.Y, back.Y, 1e-9);
    }

    [Theory]
    [InlineData(96, 96)]
    [InlineData(120, 120)]
    [InlineData(144, 144)]
    [InlineData(192, 192)]
    public void DipRect_ToPhysical_ToDip_Roundtrip(uint dpiX, uint dpiY)
    {
        var original = new DipRect(8.0, 16.0, 240.0, 320.0);
        var physical = _mapper.ToPhysical(original, dpiX, dpiY);
        var back = _mapper.ToDip(physical, dpiX, dpiY);

        Assert.Equal(original.X, back.X, 1e-9);
        Assert.Equal(original.Y, back.Y, 1e-9);
        Assert.Equal(original.Width, back.Width, 1e-9);
        Assert.Equal(original.Height, back.Height, 1e-9);
    }

    [Fact]
    public void SecondaryMonitor_DifferentDpi_MapsRelativeToMonitorOrigin()
    {
        // 主屏 1080p (96 DPI), 副屏 4K (192 DPI, 200%缩放) 位于 X=1920
        var monitorBounds = new PhysicalRect(1920, 0, 3840, 2160);
        double monitorDipLeft = 1920;
        double monitorDipTop = 0;

        // 副屏左边缘物理点 (1920, 100)
        var pt = new PhysicalPoint(1920, 100);

        // 如果不提供 monitorBounds，直接 1920 * 96/192 = 960 (错误落入主屏范围)
        var naiveDip = _mapper.ToDip(pt, 192, 192);
        Assert.Equal(960.0, naiveDip.X);

        // 提供 monitorBounds：按相对偏移 DipX = 1920 + (1920 - 1920) * 0.5 = 1920 (正确落入副屏)
        var correctDip = _mapper.ToDip(pt, 192, 192, monitorBounds, monitorDipLeft, monitorDipTop);
        Assert.Equal(1920.0, correctDip.X);
        Assert.Equal(50.0, correctDip.Y);

        // 还原物理坐标
        var restored = _mapper.ToPhysical(correctDip, 192, 192, monitorBounds, monitorDipLeft, monitorDipTop);
        Assert.Equal(pt, restored);
    }

    [Fact]
    public void SecondaryMonitor_Rect_RoundtripWithMonitorInfo()
    {
        var monitorBounds = new PhysicalRect(1920, 0, 1920, 1080);
        var monitor = new MonitorInfo(
            new MonitorId(new IntPtr(2), @"\\.\DISPLAY2"),
            @"\\.\DISPLAY2",
            monitorBounds,
            monitorBounds,
            144,
            144,
            false);

        var originalRect = new PhysicalRect(2000, 100, 300, 150);
        var dipRect = _mapper.ToDip(originalRect, monitor);
        var backToPhysical = _mapper.ToPhysical(dipRect, monitor);

        Assert.Equal(originalRect, backToPhysical);
    }
}
