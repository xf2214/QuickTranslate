namespace QuickTranslate.Infrastructure.Logging;

public static class LoggingSwitch
{
    public static bool IsDebugEnabled { get; set; } = false;

    public static event EventHandler<bool>? DebugEnabledChanged;

    public static void SetDebugEnabled(bool enabled)
    {
        if (IsDebugEnabled != enabled)
        {
            IsDebugEnabled = enabled;
            DebugEnabledChanged?.Invoke(null, enabled);
        }
    }
}
