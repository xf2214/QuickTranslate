using System.Windows.Threading;
using QuickTranslate.App.Coordination;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.App.Services;

/// <summary>
/// WPF 加载指示器：在锚点（鼠标/选区）下方显示深色 pill + 三点跳动动画。
/// 单窗口复用；所有调用自动 marshal 到 UI 线程。
/// </summary>
public class WpfStatusIndicatorService : IStatusIndicatorService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly IMonitorService _monitorService;
    private readonly object _lock = new();
    private StatusIndicatorWindow? _window;

    public WpfStatusIndicatorService(IDpiMapper dpiMapper, IMonitorService monitorService)
    {
        _dpiMapper = dpiMapper;
        _monitorService = monitorService;
    }

    private static void RunOnUi(Action a)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.CheckAccess()) a();
        else dispatcher.Invoke(DispatcherPriority.Normal, a);
    }

    public void Show(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string message)
    {
        RunOnUi(() =>
        {
            var window = EnsureWindow();
            window.SetMessage(message);

            // 放在锚点下方 10 物理像素处；放不下时放上方；再钳制到显示器工作区
            var monitors = _monitorService.EnumerateMonitors();
            var monitor = monitors.FirstOrDefault(m => m.Id == monitorId)
                          ?? _monitorService.TryGetPrimary()
                          ?? new MonitorInfo(monitorId, string.Empty,
                              new PhysicalRect(0, 0, 1920, 1080),
                              new PhysicalRect(0, 0, 1920, 1080),
                              dpiX, dpiY, true);
            var work = monitor.WorkArea;

            // SizeToContent 宽高在显示前不可知，用估算值（pill 约 150x36 DIP）
            double estW = 150.0 * dpiX / 96.0;
            double estH = 36.0 * dpiY / 96.0;
            double belowY = anchorBox.Bottom + 10;
            double aboveY = anchorBox.Top - 10 - estH;
            double phyY = belowY + estH <= work.Bottom ? belowY : Math.Max(work.Top, aboveY);
            double phyX = Math.Clamp(
                anchorBox.X + anchorBox.Width / 2.0 - estW / 2.0,
                work.Left, Math.Max(work.Left, work.Right - estW));

            // 物理像素定位（SizeToContent 窗口：不固定 DIP 宽高，内容自适应尺寸）：
            // 绕开 WPF DIP 换算依赖窗口创建时刻 DPI 认知的时序问题（高缩放/混合 DPI 错位）
            WindowPhysicalPlacement.SetPhysicalBounds(
                window,
                new PhysicalRect((int)phyX, (int)phyY, (int)estW, (int)estH),
                dpiX, dpiY, padDip: 0, topmost: true, setDipSize: false);

            if (!window.IsVisible)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                }
                _ = hwnd; // EnsureHandle 触发 OnSourceInitialized → 点击穿透样式
                window.Show();
            }
            window.StartDotsAnimation();
        });
    }

    public void Update(string message)
    {
        RunOnUi(() =>
        {
            if (_window == null || !_window.IsVisible) return;
            _window.SetMessage(message);
        });
    }

    public void Hide()
    {
        RunOnUi(() =>
        {
            if (_window == null || !_window.IsVisible) return;
            _window.StopDotsAnimation();
            _window.Hide();
        });
    }

    private StatusIndicatorWindow EnsureWindow()
    {
        lock (_lock)
        {
            _window ??= new StatusIndicatorWindow();
            return _window;
        }
    }
}
