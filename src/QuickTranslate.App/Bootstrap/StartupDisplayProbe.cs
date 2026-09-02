using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuickTranslate.App.Services;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.App.Bootstrap;

/// <summary>
/// 启动时显示器检测与自适应（UI 线程调用）：
/// 1) 枚举监视器拓扑并输出诊断日志（deviceName / Bounds / WorkArea / DPI / 主屏标记）；
/// 2) 预热每块屏的选区 overlay 窗口（EnsureHandle + 1x1 物理定位，不可见），
///    使 WPF 窗口 DPI 与所在屏同步——首次热键零创建延迟、零 DPI 认知错位；
/// 3) 挂隐藏消息窗口监听 WM_DISPLAYCHANGE：运行中改分辨率/缩放/接拔显示器时
///    重新预热（DPI 变化的窗口重建、新屏补建）并清理失效监视器的缓存窗口。
/// </summary>
public static class StartupDisplayProbe
{
    private const int WM_DISPLAYCHANGE = 0x007E;

    private static HwndSource? _watcherSource;

    public static void Run(IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("QuickTranslate.App.Bootstrap.StartupDisplayProbe");
        var monitorService = services.GetRequiredService<IMonitorService>();
        var overlayService = services.GetRequiredService<WpfSelectionOverlayService>();

        var monitors = monitorService.EnumerateMonitors();
        foreach (var monitor in monitors)
        {
            logger.LogInformation(
                "DisplayProbe: {Device} Primary={IsPrimary} Bounds=({X},{Y},{W}x{H}) Work=({WX},{WY},{WW}x{WH}) DPI=({DpiX},{DpiY})",
                monitor.DeviceName, monitor.IsPrimary,
                monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height,
                monitor.WorkArea.X, monitor.WorkArea.Y, monitor.WorkArea.Width, monitor.WorkArea.Height,
                monitor.DpiX, monitor.DpiY);
        }

        overlayService.WarmupForMonitors(monitors);
        logger.LogInformation("DisplayProbe: overlay windows warmed up for {Count} monitor(s)", monitors.Count);

        StartTopologyWatcher(logger, monitorService, overlayService);
    }

    /// <summary>
    /// 拓扑监听窗口参数：HwndSourceParameters 默认样式为 WS_OVERLAPPEDWINDOW|WS_VISIBLE，
    /// 会创建一个可见的空白标题栏窗口（左上角 136x39）——启动时"弹出窗口"的根因。
    /// 消息监听窗口只需 0 样式（不可见、无边框），WM_DISPLAYCHANGE 系统广播仍送达所有顶层窗口。
    /// </summary>
    internal static HwndSourceParameters CreateWatcherParameters() => new("QuickTranslate.DisplayWatcher")
    {
        Width = 0,
        Height = 0,
        PositionX = 0,
        PositionY = 0,
        WindowStyle = 0,
    };

    private static void StartTopologyWatcher(
        ILogger logger,
        IMonitorService monitorService,
        WpfSelectionOverlayService overlayService)
    {
        if (_watcherSource != null) return; // 幂等：重复 Run 只挂一次监听

        var source = new HwndSource(CreateWatcherParameters());
        source.AddHook(OnWindowMessage);
        _watcherSource = source;

        IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DISPLAYCHANGE)
            {
                try
                {
                    var monitors = monitorService.EnumerateMonitors();
                    logger.LogInformation(
                        "DisplayChange: display topology changed, {Count} monitor(s) now active", monitors.Count);
                    foreach (var monitor in monitors)
                    {
                        logger.LogInformation(
                            "DisplayChange: {Device} Primary={IsPrimary} Bounds=({X},{Y},{W}x{H}) DPI=({DpiX},{DpiY})",
                            monitor.DeviceName, monitor.IsPrimary,
                            monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height,
                            monitor.DpiX, monitor.DpiY);
                    }

                    // 重新预热：DPI 变化的窗口按 AreClose 比对重建、新接入的屏补建；
                    // 再清理已拔掉/失效监视器的缓存窗口。
                    overlayService.WarmupForMonitors(monitors);
                    overlayService.PruneExcept(monitors.Select(m => m.Id).ToList());
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "DisplayChange handler failed (non-fatal)");
                }
            }

            return IntPtr.Zero;
        }
    }
}
