using System.Threading;
using System.Windows;
using System.Windows.Interop;
using QuickTranslate.App.Services;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.App;

public class SelectionOverlayTests
{
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;

    [Fact]
    public void Case1_ExtendedStyleCheck()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var window = new SelectionOverlayWindow();
                new WindowInteropHelper(window).EnsureHandle();

                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                IntPtr styleIntPtr = WindowStyleHelper.GetExtendedStyle(hwnd);
                uint style = unchecked((uint)(ulong)styleIntPtr.ToInt64());

                Assert.True((style & WS_EX_NOACTIVATE) == WS_EX_NOACTIVATE,
                    $"WS_EX_NOACTIVATE not set. Style=0x{style:X8}");
                Assert.True((style & WS_EX_TOPMOST) == WS_EX_TOPMOST,
                    $"WS_EX_TOPMOST not set. Style=0x{style:X8}");

                window.Close();
                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }

    [Fact]
    public void Case2_DpiSizeCalculationRoundtrip()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var dpiMapper = new DpiMapper();
                var service = new WpfSelectionOverlayService(dpiMapper);
                var mid = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY0");

                service.Show(new PhysicalRect(100, 100, 200, 40), mid, dpiX: 144, dpiY: 144);
                var expected144 = dpiMapper.ToDip(new PhysicalRect(100, 100, 200, 40), 144, 144);
                var actual144 = service.Inspect()[mid].lastDipRect;

                Assert.Equal(expected144.X, actual144.X, 1e-9);
                Assert.Equal(expected144.Y, actual144.Y, 1e-9);
                Assert.Equal(expected144.Width, actual144.Width, 1e-9);
                Assert.Equal(expected144.Height, actual144.Height, 1e-9);

                service.Show(new PhysicalRect(100, 100, 200, 40), mid, dpiX: 120, dpiY: 120);
                var expected120 = dpiMapper.ToDip(new PhysicalRect(100, 100, 200, 40), 120, 120);
                var actual120 = service.Inspect()[mid].lastDipRect;

                Assert.Equal(expected120.X, actual120.X, 1e-9);
                Assert.Equal(expected120.Y, actual120.Y, 1e-9);
                Assert.Equal(expected120.Width, actual120.Width, 1e-9);
                Assert.Equal(expected120.Height, actual120.Height, 1e-9);

                service.HideAll();
                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }

    [Fact]
    public void Case3_HideAll_Hide_Behavior()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var dpiMapper = new DpiMapper();
                var service = new WpfSelectionOverlayService(dpiMapper);
                var mid1 = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY0");
                var mid2 = new MonitorId(new IntPtr(2), "\\\\.\\DISPLAY1");

                service.Show(new PhysicalRect(100, 100, 200, 40), mid1, dpiX: 96, dpiY: 96);
                service.Show(new PhysicalRect(300, 300, 150, 50), mid2, dpiX: 96, dpiY: 96);

                var inspect = service.Inspect();
                Assert.Equal(2, inspect.Count);
                Assert.True(inspect.ContainsKey(mid1));
                Assert.True(inspect.ContainsKey(mid2));
                Assert.NotEqual(IntPtr.Zero, inspect[mid1].hwnd);
                Assert.NotEqual(IntPtr.Zero, inspect[mid2].hwnd);

                service.HideAll();

                var field = typeof(WpfSelectionOverlayService).GetField("_windows",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(field);
                var windows = (Dictionary<MonitorId, SelectionOverlayWindow>)field!.GetValue(service)!;
                Assert.All(windows.Values, w => Assert.False(w.IsVisible));

                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }

    [Fact]
    public void Case4_HideSingleMonitor_Behavior()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var dpiMapper = new DpiMapper();
                var service = new WpfSelectionOverlayService(dpiMapper);
                var mid1 = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY0");
                var mid2 = new MonitorId(new IntPtr(2), "\\\\.\\DISPLAY1");

                service.Show(new PhysicalRect(100, 100, 200, 40), mid1, dpiX: 96, dpiY: 96);
                service.Show(new PhysicalRect(300, 300, 150, 50), mid2, dpiX: 96, dpiY: 96);

                service.Hide(mid1);

                var field = typeof(WpfSelectionOverlayService).GetField("_windows",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(field);
                var windows = (Dictionary<MonitorId, SelectionOverlayWindow>)field!.GetValue(service)!;
                Assert.False(windows[mid1].IsVisible, "mid1 should be hidden");
                Assert.True(windows[mid2].IsVisible, "mid2 should still be visible");

                service.HideAll();
                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }

    [Fact]
    public void Case5_Update_UpdatesLayout()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var dpiMapper = new DpiMapper();
                var service = new WpfSelectionOverlayService(dpiMapper);
                var mid = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY0");

                service.Show(new PhysicalRect(100, 100, 200, 40), mid, dpiX: 96, dpiY: 96);
                var first = service.Inspect()[mid].lastDipRect;

                service.Update(new PhysicalRect(200, 200, 300, 80), mid, dpiX: 96, dpiY: 96);
                var updated = service.Inspect()[mid].lastDipRect;
                var expected = dpiMapper.ToDip(new PhysicalRect(200, 200, 300, 80), 96, 96);

                Assert.NotEqual(first, updated);
                Assert.Equal(expected.X, updated.X, 1e-9);
                Assert.Equal(expected.Y, updated.Y, 1e-9);
                Assert.Equal(expected.Width, updated.Width, 1e-9);
                Assert.Equal(expected.Height, updated.Height, 1e-9);

                service.HideAll();
                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }

    [Fact]
    public void Case6_Dpi96_BaselineCalculation()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var dpiMapper = new DpiMapper();
                var service = new WpfSelectionOverlayService(dpiMapper);
                var mid = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY0");

                service.Show(new PhysicalRect(96, 192, 960, 480), mid, dpiX: 96, dpiY: 96);
                var result = service.Inspect()[mid].lastDipRect;

                Assert.Equal(96.0, result.X, 1e-9);
                Assert.Equal(192.0, result.Y, 1e-9);
                Assert.Equal(960.0, result.Width, 1e-9);
                Assert.Equal(480.0, result.Height, 1e-9);

                service.HideAll();
                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }
}
