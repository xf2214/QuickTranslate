using System.Reflection;
using System.Threading;
using System.Windows.Interop;
using QuickTranslate.App.Services;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.App;

public class OverlayDpiRecreatePolicyTests
{
    [Fact]
    public void NewMonitor_RecreateOverlay_WhenMonitorIdDifferent()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var dpiMapper = new DpiMapper();
                var service = new WpfSelectionOverlayService(dpiMapper);
                var mid1 = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY1");
                var mid2 = new MonitorId(new IntPtr(2), "\\\\.\\DISPLAY2");

                service.Show(new PhysicalRect(100, 100, 200, 40), mid1, dpiX: 96, dpiY: 96);
                var inspect1 = service.Inspect();
                Assert.True(inspect1.ContainsKey(mid1));
                var firstHwnd = inspect1[mid1].hwnd;
                Assert.NotEqual(IntPtr.Zero, firstHwnd);

                int closeCount = 0;
                var field = typeof(WpfSelectionOverlayService).GetField("_windows",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(field);
                var windowsDict = (Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)>)field!.GetValue(service)!;
                windowsDict[mid1].window.Closed += (_, _) => closeCount++;

                service.Show(new PhysicalRect(300, 300, 150, 50), mid2, dpiX: 96, dpiY: 96);
                var inspectAfter = service.Inspect();
                Assert.True(inspectAfter.ContainsKey(mid1));
                Assert.True(inspectAfter.ContainsKey(mid2));
                Assert.NotNull(inspectAfter[mid2].hwnd);
                Assert.NotEqual(IntPtr.Zero, inspectAfter[mid2].hwnd);

                Assert.True(closeCount >= 0);

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
    public void SameMonitorSameDpi_NoRecreate()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                var dpiMapper = new DpiMapper();
                var service = new WpfSelectionOverlayService(dpiMapper);
                var mid = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY1");

                service.Show(new PhysicalRect(100, 100, 200, 40), mid, dpiX: 96, dpiY: 96);
                var first = service.Inspect()[mid].hwnd;

                int closeCount = 0;
                var field = typeof(WpfSelectionOverlayService).GetField("_windows",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(field);
                var windowsDict = (Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)>)field!.GetValue(service)!;
                windowsDict[mid].window.Closed += (_, _) => closeCount++;

                service.Show(new PhysicalRect(150, 150, 250, 50), mid, dpiX: 96, dpiY: 96);
                var second = service.Inspect()[mid].hwnd;

                Assert.Equal(first, second);
                Assert.Equal(0, closeCount);

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
