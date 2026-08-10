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
}
