using System.Drawing;
using System.Drawing.Imaging;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class ScreenFrameIntegrityTests
{
    [Fact]
    public void ScreenFrame_Construct_RegionMatchesBitmap()
    {
        using var bmp = new Bitmap(200, 100, PixelFormat.Format32bppArgb);
        var region = new PhysicalRect(0, 0, 200, 100);
        var monitorId = MonitorId.Empty;

        using var frame = new ScreenFrame(bmp, region, monitorId);

        Assert.Equal(200, frame.Region.Width);
        Assert.Equal(100, frame.Region.Height);
        Assert.Equal(bmp.Width, frame.Region.Width);
        Assert.Equal(bmp.Height, frame.Region.Height);
    }

    [Fact]
    public void ScreenFrame_Dispose_Twice_DoesNotThrow()
    {
        var bmp = new Bitmap(200, 100, PixelFormat.Format32bppArgb);
        var region = new PhysicalRect(0, 0, 200, 100);
        var monitorId = MonitorId.Empty;
        var frame = new ScreenFrame(bmp, region, monitorId);

        var ex1 = Record.Exception(() => ((IDisposable)frame).Dispose());
        var ex2 = Record.Exception(() => ((IDisposable)frame).Dispose());

        Assert.Null(ex1);
        Assert.Null(ex2);
    }
}
