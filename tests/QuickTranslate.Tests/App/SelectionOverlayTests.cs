using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    private static readonly object _syncLock = new();

    [Fact]
    public void Case1_ExtendedStyleCheck()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                lock (_syncLock)
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
                }
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
                lock (_syncLock)
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
                }
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
                lock (_syncLock)
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
                    var windows = (Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)>)field!.GetValue(service)!;
                    Assert.All(windows.Values, v => Assert.False(v.window.IsVisible));
                }

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
                lock (_syncLock)
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
                    var windows = (Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)>)field!.GetValue(service)!;
                    Assert.False(windows[mid1].window.IsVisible, "mid1 should be hidden");
                    Assert.True(windows[mid2].window.IsVisible, "mid2 should still be visible");

                    service.HideAll();
                }
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
                lock (_syncLock)
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
                }
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
                lock (_syncLock)
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
                }
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

    [Theory]
    [InlineData(15.0, 8.0)]    // 15 * 0.35 = 5.25 -> clamped to 8.0
    [InlineData(20.0, 8.0)]    // 20 * 0.35 = 7.0 -> clamped to 8.0
    [InlineData(30.0, 10.5)]   // 30 * 0.35 = 10.5
    [InlineData(45.0, 15.75)]  // 45 * 0.35 = 15.75
    [InlineData(80.0, 28.0)]   // 80 * 0.35 = 28.0
    [InlineData(100.0, 32.0)]  // 100 * 0.35 = 35.0 -> clamped to 32.0
    [InlineData(740.0, 32.0)]  // 740 * 0.35 = 259.0 -> clamped to 32.0
    public void Case7_ScanLine_AdaptiveWidth(double boxWidth, double expectedScanLineWidth)
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    var window = new SelectionOverlayWindow();
                    new WindowInteropHelper(window).EnsureHandle();

                    window.ApplyPhysicalLayout(new PhysicalRect(50, 50, (int)boxWidth, 24), 96, 96);

                    Assert.Equal(expectedScanLineWidth, window.ScanLine.Width, 1e-4);

                    window.Close();
                }
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

        if (error != null) throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        Assert.True(passed);
    }

    [Fact]
    public void Case8_ScanPanel_HasPermanentHighContrastStroke_AndCornerRadiusClip()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    var window = new SelectionOverlayWindow();
                    new WindowInteropHelper(window).EnsureHandle();

                    window.ApplyLayout(new DipRect(100, 100, 200, 40));

                    // 验证精致常驻描边 (Point 2)
                    var strokeBrush = window.ScanBackground.Stroke as SolidColorBrush;
                    Assert.NotNull(strokeBrush);
                    Assert.Equal(Color.FromArgb(0x4D, 0x00, 0x7A, 0xFF), strokeBrush.Color);
                    Assert.Equal(1.5, window.ScanBackground.StrokeThickness);
                    Assert.Equal(4.0, window.ScanBackground.RadiusX);
                    Assert.Equal(4.0, window.ScanBackground.RadiusY);

                    // 验证圆角裁剪 (Point 3)
                    Assert.Equal(new CornerRadius(4), window.ScanLineContainer.CornerRadius);
                    var clipGeom = window.ScanLineContainer.Clip as RectangleGeometry;
                    Assert.NotNull(clipGeom);
                    Assert.Equal(4.0, clipGeom.RadiusX);
                    Assert.Equal(4.0, clipGeom.RadiusY);
                    Assert.Equal(new Rect(0, 0, 200, 40), clipGeom.Rect);

                    window.Close();
                }
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

        if (error != null) throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        Assert.True(passed);
    }

    [Fact]
    public void Case9_BeginScanSweep_UsesTargetLayoutRectWidth_NotStaleActualWidth()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    var window = new SelectionOverlayWindow();
                    new WindowInteropHelper(window).EnsureHandle();

                    // 模拟大预览框（740px）
                    window.SetPreviewMode(true);
                    window.ApplyPhysicalLayout(new PhysicalRect(0, 0, 740, 320), 96, 96);
                    window.Show();

                    // 切换为小词选框（45px）
                    window.SetPreviewMode(false);
                    window.ApplyPhysicalLayout(new PhysicalRect(100, 100, 45, 20), 96, 96);

                    // TargetLayoutRect 必须是权威的 45px
                    Assert.Equal(45.0, window.TargetLayoutRect.Width);
                    Assert.Equal(45.0, window.LastLayoutRect.Width);

                    // 扫描线宽度必须自适应 45 * 0.35 = 15.75
                    Assert.Equal(15.75, window.ScanLine.Width, 1e-4);

                    // 裁剪区域也同步对齐 45x20
                    var clipGeom = window.ScanLineContainer.Clip as RectangleGeometry;
                    Assert.NotNull(clipGeom);
                    Assert.Equal(new Rect(0, 0, 45, 20), clipGeom.Rect);

                    window.Close();
                }
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

        if (error != null) throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        Assert.True(passed);
    }

    [Fact]
    public void Case10_StatusIndicator_AvoidsTargetText_AndLeavesBreathingRoom()
    {
        var workArea = new PhysicalRect(0, 0, 1920, 1040);
        uint dpiX = 96;
        uint dpiY = 96;

        // 1. 小光标锚点（8x8）：偏向右下方，不遮挡光标所指文字
        var cursorAnchor = new PhysicalRect(500, 300, 8, 8);
        var (x1, y1, w1, h1) = WpfStatusIndicatorService.CalculatePlacement(cursorAnchor, workArea, dpiX, dpiY);

        // 验证 X 偏向右下（preferredX = anchor.X + 16 = 516 > 500）
        Assert.True(x1 > cursorAnchor.X, $"Indicator X ({x1}) should be offset to the right of cursor anchor ({cursorAnchor.X})");
        Assert.Equal(516, x1);

        // 验证 Y 留足垂直呼吸空间（>= 18px 间距，不压在文本行上）
        Assert.True(y1 - cursorAnchor.Bottom >= 18, $"Vertical gap ({y1 - cursorAnchor.Bottom}) should be >= 18px");
        Assert.Equal(326, y1); // 308 + 18 = 326

        // 2. 较宽选区（200x30）：保持居中对齐，但依然留足垂直呼吸空间
        var blockAnchor = new PhysicalRect(400, 300, 200, 30);
        var (x2, y2, w2, h2) = WpfStatusIndicatorService.CalculatePlacement(blockAnchor, workArea, dpiX, dpiY);
        // estW = 150. 居中: 400 + 100 - 75 = 425
        Assert.Equal(425, x2);
        Assert.True(y2 - blockAnchor.Bottom >= 18);
        Assert.Equal(348, y2); // 330 + 18 = 348

        // 3. 靠近屏幕底部的锚点：翻转到上方避让
        var bottomAnchor = new PhysicalRect(500, 1010, 8, 8);
        var (x3, y3, w3, h3) = WpfStatusIndicatorService.CalculatePlacement(bottomAnchor, workArea, dpiX, dpiY);
        Assert.True(y3 < bottomAnchor.Top, $"Indicator should flip above anchor near screen bottom. y={y3}, top={bottomAnchor.Top}");
        // aboveY = 1010 - 18 - 36 = 956
        Assert.Equal(956, y3);
    }    [Fact]
    public void Case11_DragState_TogglesScanLineContainerVisibility()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    var window = new SelectionOverlayWindow();
                    new WindowInteropHelper(window).EnsureHandle();

                    // 1) 拖拽状态下：显示半透明蓝色选区，隐藏扫描线，防止拖拽期间闪烁扫描
                    window.SetDragState(true);
                    Assert.True(window.IsDragging);
                    Assert.Equal(Visibility.Visible, window.ScanPanel.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.ScanLineContainer.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.PreviewBorder.Visibility);

                    // 2) 拖拽结束（松开确认）：恢复扫描线容器可见性，允许单次扫描
                    window.SetDragState(false);
                    Assert.False(window.IsDragging);
                    Assert.Equal(Visibility.Visible, window.ScanPanel.Visibility);
                    Assert.Equal(Visibility.Visible, window.ScanLineContainer.Visibility);

                    window.Close();
                }
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

        if (error != null) throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        Assert.True(passed);
    }

    [Fact]
    public void Case12_SelectionOverlayWindow_Implements_IPhysicalPlacementAware()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    var window = new SelectionOverlayWindow();
                    new WindowInteropHelper(window).EnsureHandle();

                    Assert.IsAssignableFrom<IPhysicalPlacementAware>(window);
                    var aware = (IPhysicalPlacementAware)window;

                    var box = new PhysicalRect(2000, 100, 300, 80);
                    var monBounds = new PhysicalRect(1920, 0, 1920, 1080);
                    window.ApplyPhysicalLayout(box, 144, 144, monBounds, 1920.0, 0.0);

                    Assert.Equal(box, aware.LastPhysicalBox);
                    Assert.Equal(144u, aware.LastDpiX);
                    Assert.Equal(144u, aware.LastDpiY);
                    Assert.Equal(monBounds, aware.LastMonitorBounds);
                    Assert.Equal(1920.0, aware.LastMonitorDipLeft);

                    window.Close();
                }
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

        if (error != null) throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        Assert.True(passed);
    }
}
