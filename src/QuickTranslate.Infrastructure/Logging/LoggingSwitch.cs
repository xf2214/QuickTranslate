using Serilog.Core;
using Serilog.Events;

namespace QuickTranslate.Infrastructure.Logging;

/// <summary>
/// Debug 日志运行时开关。同时驱动两处：
/// 1. <see cref="Level"/>（Serilog LoggingLevelSwitch）：由 LoggerConfigurator 以
///    MinimumLevel.ControlledBy 接管，运行时切换 Verbose/Information 无需重建日志器；
/// 2. <see cref="IsDebugEnabled"/>：FilteringLoggerProvider 等过滤逻辑使用。
/// 设置为异步加载（Build 不阻塞），加载完成后须再次调用 <see cref="SetDebugEnabled"/>
/// 把落盘值同步到本开关，否则启动竞态会用默认 Information 锁死整个进程。
/// </summary>
public static class LoggingSwitch
{
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    /// <summary>Serilog 动态最低级别开关（仅供 LoggerConfigurator 接线）。</summary>
    internal static LoggingLevelSwitch Level => LevelSwitch;

    public static bool IsDebugEnabled { get; private set; } = false;

    public static event EventHandler<bool>? DebugEnabledChanged;

    public static void SetDebugEnabled(bool enabled)
    {
        // 先切级别再翻标志：保证订阅者在事件里看到的状态一致。
        LevelSwitch.MinimumLevel = enabled ? LogEventLevel.Verbose : LogEventLevel.Information;
        if (IsDebugEnabled != enabled)
        {
            IsDebugEnabled = enabled;
            DebugEnabledChanged?.Invoke(null, enabled);
        }
    }
}
