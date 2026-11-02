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

    public static (int x, int y, int w, int h) CalculatePlacement(
        PhysicalRect anchorBox, PhysicalRect workArea, uint dpiX, uint dpiY)
    {
        // SizeToContent 宽高在显示前不可知，用估算值（pill 约 150x36 DIP）
        double estW = 150.0 * dpiX / 96.0;
        double estH = 36.0 * dpiY / 96.0;

        // 避让文字：留足呼吸空间（约 18 DIP 垂直空隙，避开当前文本行及光标箭头）；
        // 放不下时放上方；再钳制到显示器工作区
        double vGap = Math.Round(18.0 * dpiY / 96.0);
        double belowY = anchorBox.Bottom + vGap;
        double aboveY = anchorBox.Top - vGap - estH;
        double phyY = belowY + estH <= workArea.Bottom ? belowY : Math.Max(workArea.Top, aboveY);

        // 当锚点较小（如光标 8x8）时偏向右下，避免深色指示气泡居中正压在光标所指目标文字上；
        // 较宽选区时保持居中对齐
        double hOffset = Math.Round(16.0 * dpiX / 96.0);
        double preferredX = anchorBox.Width <= 48
            ? (anchorBox.X + hOffset)
            : (anchorBox.X + anchorBox.Width / 2.0 - estW / 2.0);
        double phyX = Math.Clamp(
            preferredX,
            workArea.Left, Math.Max(workArea.Left, workArea.Right - estW));

        return ((int)phyX, (int)phyY, (int)estW, (int)estH);
    }

    public void Show(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string message)
    {
        RunOnUi(() =>
        {
            var window = EnsureWindow();
            window.SetMessage(message);

            var monitors = _monitorService.EnumerateMonitors();
            var monitor = monitors.FirstOrDefault(m => m.Id == monitorId)
                          ?? _monitorService.TryGetPrimary()
                          ?? new MonitorInfo(monitorId, string.Empty,
                              PhysicalRect.Fallback1080p,
                              PhysicalRect.Fallback1080p,
                              dpiX, dpiY, true);

            var (phyX, phyY, estW, estH) = CalculatePlacement(anchorBox, monitor.WorkArea, dpiX, dpiY);

            // 物理像素定位（SizeToContent 窗口：不固定 DIP 宽高，内容自适应尺寸）：
            // 绕开 WPF DIP 换算依赖窗口创建时刻 DPI 认知的时序问题（高缩放/混合 DPI 错位）
            WindowPhysicalPlacement.SetPhysicalBounds(
                window,
                new PhysicalRect(phyX, phyY, estW, estH),
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