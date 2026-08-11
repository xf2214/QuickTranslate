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
