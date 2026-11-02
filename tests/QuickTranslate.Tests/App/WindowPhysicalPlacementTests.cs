using System.Threading;
using System.Windows;
using System.Windows.Interop;
using QuickTranslate.App.Coordination;
using QuickTranslate.App.Services;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.UnmanagedMethods;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.App;

/// <summary>
/// 物理像素定位验证：SetWindowPos 写入的物理几何是最终权威值，
/// 与 WPF 窗口 DPI 认知无关——这正是高缩放/混合 DPI 下选区框精确定位的根基。
/// </summary>
public class WindowPhysicalPlacementTests
{
    private static void RunOnSta(Action action)
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                action();
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

    private static RECT GetRect(IntPtr hwnd)
    {
        Assert.True(User32.GetWindowRect(hwnd, out RECT rect), "GetWindowRect failed");
        return rect;
    }

    [Theory]
    [InlineData(96u)]
    [InlineData(144u)]
    [InlineData(192u)]
    public void Case1_PhysicalBounds_AppliedExactly_RegardlessOfDpi(uint dpi)
    {
        RunOnSta(() =>
        {
            var window = new Window();
            try
            {
                var bounds = new PhysicalRect(300, 200, 400, 150);
                bool ok = WindowPhysicalPlacement.SetPhysicalBounds(window, bounds, dpi, dpi, padDip: 0, topmost: false);

                Assert.True(ok, "SetWindowPos failed");
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, hwnd);

                // 物理写入是权威值：与测试机 DPI 无关，HWND 几何精确等于传入物理边界
                RECT rect = GetRect(hwnd);
                Assert.Equal(bounds.X, rect.Left);
                Assert.Equal(bounds.Y, rect.Top);
                Assert.Equal(bounds.X + bounds.Width, rect.Right);
                Assert.Equal(bounds.Y + bounds.Height, rect.Bottom);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Case2_PadDip_ConvertedToPhysicalPadding()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            try
            {
                var bounds = new PhysicalRect(100, 100, 200, 40);
                bool ok = WindowPhysicalPlacement.SetPhysicalBounds(window, bounds, 144, 144, padDip: 2, topmost: false);

                Assert.True(ok);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;

                // padDip=2 @144dpi → 物理 pad = 3px
                RECT rect = GetRect(hwnd);
                Assert.Equal(bounds.X - 3, rect.Left);
                Assert.Equal(bounds.Y - 3, rect.Top);
                Assert.Equal(bounds.X + bounds.Width + 3, rect.Right);
                Assert.Equal(bounds.Y + bounds.Height + 3, rect.Bottom);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Case3_SizeToContentMode_SkipsDipSizeButPositionsPhysically()
    {
        RunOnSta(() =>
        {
            var window = new Window { SizeToContent = SizeToContent.WidthAndHeight };
            try
            {
                var bounds = new PhysicalRect(250, 250, 320, 120);
                bool ok = WindowPhysicalPlacement.SetPhysicalBounds(
                    window, bounds, 144, 144, padDip: 0, topmost: true, setDipSize: false);

                Assert.True(ok);

                // setDipSize=false：helper 不写 Width/Height（尺寸仍由 SizeToContent 内容自适应，
                // EnsureHandle 过程 WPF 可能自行写入测量值，非本 helper 行为），位置精确物理
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                RECT rect = GetRect(hwnd);
                Assert.Equal(bounds.X, rect.Left);
                Assert.Equal(bounds.Y, rect.Top);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Case4_OverlayWindow_PhysicalLayoutAndDipSemantics()
    {
        RunOnSta(() =>
        {
            var dpiMapper = new DpiMapper();
            var service = new WpfSelectionOverlayService(dpiMapper);
            var mid = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY0");
            try
            {
                var physicalBox = new PhysicalRect(500, 300, 200, 60);
                service.Show(physicalBox, mid, dpiX: 144, dpiY: 144);

                // DIP 语义保留（与 SelectionOverlayTests 基线一致）
                var expectedDip = dpiMapper.ToDip(physicalBox, 144, 144);
                var actualDip = service.Inspect()[mid].lastDipRect;
                Assert.Equal(expectedDip.X, actualDip.X, 1e-9);
                Assert.Equal(expectedDip.Y, actualDip.Y, 1e-9);

                // HWND 物理几何精确：padDip=2 @144dpi → ±3 物理像素
                IntPtr hwnd = service.Inspect()[mid].hwnd;
                Assert.NotEqual(IntPtr.Zero, hwnd);
                RECT rect = GetRect(hwnd);
                Assert.Equal(physicalBox.X - 3, rect.Left);
                Assert.Equal(physicalBox.Y - 3, rect.Top);
                Assert.Equal(physicalBox.X + physicalBox.Width + 3, rect.Right);
                Assert.Equal(physicalBox.Y + physicalBox.Height + 3, rect.Bottom);
            }
            finally
            {
                service.PruneExcept(Array.Empty<MonitorId>());
            }
        });
    }

    [Fact]
    public void Case5_WarmupForMonitors_CreatesWindowsPerMonitor_PruneRemovesStale()
    {
        RunOnSta(() =>
        {
            var dpiMapper = new DpiMapper();
            var service = new WpfSelectionOverlayService(dpiMapper);
            try
            {
                var mid1 = new MonitorId(new IntPtr(1), "\\\\.\\DISPLAY1");
                var mid2 = new MonitorId(new IntPtr(2), "\\\\.\\DISPLAY2");
                var monitors = new List<MonitorInfo>
                {
                    new(mid1, "\\\\.\\DISPLAY1",
                        new PhysicalRect(0, 0, 3000, 2000), new PhysicalRect(0, 0, 3000, 2000),
                        144, 144, true),
                    new(mid2, "\\\\.\\DISPLAY2",
                        new PhysicalRect(3000, 0, 1920, 1200), new PhysicalRect(3000, 0, 1920, 1200),
                        96, 96, false),
                };

                service.WarmupForMonitors(monitors);

                var field = typeof(WpfSelectionOverlayService).GetField("_windows",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(field);
                var windows = Assert.IsType<Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)>>(field!.GetValue(service));
                Assert.Equal(2, windows.Count);
                Assert.Equal(144u, windows[mid1].dpiX);
                Assert.Equal(96u, windows[mid2].dpiX);
                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(windows[mid1].window).Handle);
                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(windows[mid2].window).Handle);

                // 幂等：重复预热（DPI 一致）不重建窗口
                service.WarmupForMonitors(monitors);
                var windowsAfter = Assert.IsType<Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)>>(field.GetValue(service));
                Assert.Equal(2, windowsAfter.Count);
                Assert.Same(windows[mid1].window, windowsAfter[mid1].window);

                // 拔掉 DISPLAY2：Prune 移除失效监视器窗口
                service.PruneExcept(new[] { mid1 });
                var windowsPruned = Assert.IsType<Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)>>(field.GetValue(service));
                Assert.Single(windowsPruned);
                Assert.Contains(mid1, windowsPruned.Keys);
                Assert.DoesNotContain(mid2, windowsPruned.Keys);
            }
            finally
            {
                service.PruneExcept(Array.Empty<MonitorId>());
            }
        });
    }
    [Fact]
    public void Case6_SecondaryMonitor_WindowLeft_ConsistentWithSetWindowPos()
    {
        RunOnSta(() =>
        {
            var window = new WordPopupWindow();
            try
            {
                var monitorBounds = new PhysicalRect(1920, 0, 1920, 1080);
                var bounds = new PhysicalRect(2000, 100, 300, 150);
                uint dpi = 144;

                // PerMonitorDpiHelpers 按目标屏物理原点计算相对偏移与 DIP
                var (expectedDipL, expectedDipT, expectedDipW, expectedDipH) = PerMonitorDpiHelpers.ToDip(
                    bounds, dpi, dpi, monitorBounds, monitorDipLeft: 1920, monitorDipTop: 0);

                double expectedDipLeft = 1920 + (2000 - 1920) * (96.0 / 144.0);
                Assert.Equal(expectedDipLeft, expectedDipL, 2);

                bool ok = WindowPhysicalPlacement.SetPhysicalBounds(
                    window, bounds, dpi, dpi, padDip: 0, topmost: false, setDipSize: true,
                    monitorPhysicalBounds: monitorBounds, monitorDipLeft: 1920, monitorDipTop: 0);

                Assert.True(ok);

                // 物理上下文正确记录，基准与 SetWindowPos 严格一致
                Assert.Equal(bounds, window.LastPhysicalBox);
                Assert.Equal(monitorBounds, window.LastMonitorBounds);
                Assert.Equal(1920.0, window.LastMonitorDipLeft);

                // HWND 物理基准与 SetWindowPos 严格一致
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                RECT rect = GetRect(hwnd);
                Assert.Equal(bounds.X, rect.Left);
                Assert.Equal(bounds.Y, rect.Top);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Case7_WordPopupWindow_TracksLastPhysicalBox_And_DpiConvergence()
    {
        RunOnSta(() =>
        {
            var window = new WordPopupWindow();
            try
            {
                var bounds = new PhysicalRect(300, 200, 350, 150);
                bool ok = WindowPhysicalPlacement.SetPhysicalBounds(window, bounds, 96, 96, padDip: 0, topmost: false);
                Assert.True(ok);

                Assert.True(window.LastPhysicalBox.HasValue);
                Assert.Equal(bounds, window.LastPhysicalBox.Value);
                Assert.Equal(96u, window.LastDpiX);

                // 触发 DPI 变化收敛
                window.ApplyDpiConvergence(192, 192);

                Assert.Equal(192u, window.LastDpiX);
                Assert.Equal(192u, window.LastDpiY);

                // 物理定位权威收敛到 LastPhysicalBox，防止拉伸失控
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                RECT rect = GetRect(hwnd);
                Assert.Equal(bounds.X, rect.Left);
                Assert.Equal(bounds.Y, rect.Top);
                Assert.Equal(bounds.X + bounds.Width, rect.Right);
                Assert.Equal(bounds.Y + bounds.Height, rect.Bottom);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Case8_BlockPopupWindow_TracksLastPhysicalBox_And_DpiConvergence()
    {
        RunOnSta(() =>
        {
            var window = new BlockPopupWindow();
            try
            {
                var bounds = new PhysicalRect(250, 150, 440, 320);
                bool ok = WindowPhysicalPlacement.SetPhysicalBounds(window, bounds, 120, 120, padDip: 0, topmost: false);
                Assert.True(ok);

                Assert.True(window.LastPhysicalBox.HasValue);
                Assert.Equal(bounds, window.LastPhysicalBox.Value);
                Assert.Equal(120u, window.LastDpiX);

                // 触发 DPI 变化收敛
                window.ApplyDpiConvergence(144, 144);

                Assert.Equal(144u, window.LastDpiX);
                Assert.Equal(144u, window.LastDpiY);

                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                RECT rect = GetRect(hwnd);
                Assert.Equal(bounds.X, rect.Left);
                Assert.Equal(bounds.Y, rect.Top);
                Assert.Equal(bounds.X + bounds.Width, rect.Right);
                Assert.Equal(bounds.Y + bounds.Height, rect.Bottom);
            }
            finally
            {
                window.Close();
            }
        });
    }
}

