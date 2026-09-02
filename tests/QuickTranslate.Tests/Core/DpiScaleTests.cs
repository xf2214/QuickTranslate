using QuickTranslate.Core.Geometry;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class DpiScaleTests
{
    [Theory]
    [InlineData(20, 96u, 20)]   // 100% 缩放：恒等
    [InlineData(20, 144u, 30)]  // 150% 缩放
    [InlineData(20, 192u, 40)]  // 200% 缩放
    [InlineData(10, 144u, 15)]
    [InlineData(1200, 144u, 1800)]
    [InlineData(720, 192u, 1440)]
    public void Px_Scales96BaselineToTargetDpi(int px96, uint dpi, int expected)
    {
        Assert.Equal(expected, DpiScale.Px(px96, dpi));
    }

    [Theory]
    [InlineData(96u, 1.0)]
    [InlineData(144u, 1.5)]
    [InlineData(192u, 2.0)]
    public void Factor_MatchesDpiRatio(uint dpi, double expected)
    {
        Assert.Equal(expected, DpiScale.Factor(dpi), 1e-9);
    }

    [Fact]
    public void Px_DpiZero_FallsBackTo96Identity()
    {
        // MonitorService 在 GetDpiForMonitor 失败时回退 96：换算应恒等而非除零
        Assert.Equal(20, DpiScale.Px(20, 0));
    }

    [Fact]
    public void Factor_DpiZero_FallsBackToOne()
    {
        Assert.Equal(1.0, DpiScale.Factor(0), 1e-9);
    }

    [Fact]
    public void Px_RoundsAwayFromZeroOnMidpoint()
    {
        // 150% 下 21 → 31.5：四舍五入远离零 → 32
        Assert.Equal(32, DpiScale.Px(21, 144));
    }
}
