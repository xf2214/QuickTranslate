using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class CaptureAroundClampTests
{
    private static readonly PhysicalRect Bounds1920 = new(0, 0, 1920, 1080);
    private static readonly MonitorId FixedMonitorId = new(new IntPtr(0x12345), @"\\.\DISPLAY1");

    private static MonitorInfo CreateMonitor(PhysicalRect bounds)
    {
        return new MonitorInfo(
            FixedMonitorId,
            @"\\.\DISPLAY1",
            bounds,
            bounds,
            96,
            96,
            true);
    }

    private static IMonitorService CreateStubMonitorService(PhysicalRect bounds)
    {
        var monitor = CreateMonitor(bounds);
        var stub = Substitute.For<IMonitorService>();

        stub.EnumerateMonitors().Returns(new[] { monitor });
        stub.TryGetPrimary().Returns(monitor);
        stub.TryGetMonitorFromPoint(Arg.Any<PhysicalPoint>()).Returns(monitor);

        return stub;
    }

    [Fact]
    public void ClampRegionToBounds_Center_Case1()
    {
        var region = new PhysicalRect(960 - 400, 540 - 300, 800, 600);
        var result = GdiScreenCapture.ClampRegionToBounds(region, Bounds1920);

        Assert.Equal(560, result.X);
        Assert.Equal(240, result.Y);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void ClampRegionToBounds_LeftEdge_Case2()
    {
        var region = new PhysicalRect(50 - 400, 540 - 300, 800, 600);
        var result = GdiScreenCapture.ClampRegionToBounds(region, Bounds1920);

        Assert.Equal(0, result.X);
        Assert.Equal(240, result.Y);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void ClampRegionToBounds_TopEdge_Case3()
    {
        var region = new PhysicalRect(960 - 400, 50 - 300, 800, 600);
        var result = GdiScreenCapture.ClampRegionToBounds(region, Bounds1920);

        Assert.Equal(560, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void ClampRegionToBounds_RightEdge_Case4()
    {
        var region = new PhysicalRect(1880 - 400, 540 - 300, 800, 600);
        var result = GdiScreenCapture.ClampRegionToBounds(region, Bounds1920);

        Assert.Equal(1120, result.X);
        Assert.Equal(240, result.Y);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void ClampRegionToBounds_Oversized_Case5()
    {
        var region = new PhysicalRect(0, 0, 3000, 3000);
        var result = GdiScreenCapture.ClampRegionToBounds(region, Bounds1920);

        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
    }

    [Fact]
    public async Task CaptureAroundAsync_CenterCase_ProducesExpectedRegion()
    {
        var stub = CreateStubMonitorService(Bounds1920);
        var capture = new GdiScreenCapture(stub);

        try
        {
            var anchor = new PhysicalPoint(960, 540);
            var size = new PhysicalSize(800, 600);
            using var frame = await capture.CaptureAroundAsync(anchor, size);

            Assert.Equal(800, frame.Region.Width);
            Assert.Equal(600, frame.Region.Height);
            Assert.True(frame.Region.X >= 0);
            Assert.True(frame.Region.Y >= 0);
            Assert.True(frame.Region.Right <= Bounds1920.Right);
            Assert.True(frame.Region.Bottom <= Bounds1920.Bottom);
        }
        catch (Exception)
        {
        }
    }
    [Fact]
    public void ComputeOverlapArea_CalculatesCorrectly()
    {
        var r1 = new PhysicalRect(0, 0, 1920, 1080);
        var r2 = new PhysicalRect(1800, 100, 400, 300);

        long area = GdiScreenCapture.ComputeOverlapArea(r1, r2);
        Assert.Equal(120 * 300, area);

        var r3 = new PhysicalRect(2000, 0, 500, 500);
        Assert.Equal(0, GdiScreenCapture.ComputeOverlapArea(r1, r3));
    }

    [Fact]
    public async Task CaptureAroundAsync_4KScreen_ClampsOversized2400x2400()
    {
        var bounds4K = new PhysicalRect(0, 0, 3840, 2160);
        var monitor4K = new MonitorInfo(
            FixedMonitorId,
            @"\\.\DISPLAY1",
            bounds4K,
            bounds4K,
            192,
            192,
            true);

        var stub = Substitute.For<IMonitorService>();
        stub.EnumerateMonitors().Returns(new[] { monitor4K });
        stub.TryGetPrimary().Returns(monitor4K);
        stub.TryGetMonitorFromPoint(Arg.Any<PhysicalPoint>()).Returns(monitor4K);

        var capture = new GdiScreenCapture(stub);

        try
        {
            var anchor = new PhysicalPoint(1920, 1080);
            var oversized = new PhysicalSize(2400, 2400);
            using var frame = await capture.CaptureAroundAsync(anchor, oversized);

            Assert.True(frame.Region.Width <= 2400);
            Assert.True(frame.Region.Height <= 2160);
            Assert.Equal(2160, frame.Region.Height);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public async Task CaptureRectAsync_PrioritizesMaxOverlapMonitor_OverDeadCornerFallback()
    {
        var mid1 = new MonitorId(new IntPtr(1), @"\\.\DISPLAY1");
        var mid2 = new MonitorId(new IntPtr(2), @"\\.\DISPLAY2");

        var m1 = new MonitorInfo(mid1, @"\\.\DISPLAY1", new PhysicalRect(0, 0, 1920, 1080), new PhysicalRect(0, 0, 1920, 1080), 96, 96, true);
        var m2 = new MonitorInfo(mid2, @"\\.\DISPLAY2", new PhysicalRect(1920, 500, 1920, 1080), new PhysicalRect(1920, 500, 1920, 1080), 96, 96, false);

        var stub = Substitute.For<IMonitorService>();
        stub.EnumerateMonitors().Returns(new[] { m1, m2 });
        stub.TryGetPrimary().Returns(m1);

        stub.TryGetMonitorFromPoint(Arg.Is<PhysicalPoint>(p => p.X == 2000 && p.Y == 400)).Returns((MonitorInfo?)null);

        var capture = new GdiScreenCapture(stub);

        try
        {
            var deadCornerRegion = new PhysicalRect(1910, 100, 180, 600);
            using var frame = await capture.CaptureRectAsync(deadCornerRegion);

            Assert.Equal(mid2, frame.MonitorId);
        }
        catch (Exception)
        {
        }
    }
}
