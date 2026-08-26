namespace QuickTranslate.Core.Options;

public readonly record struct HotkeyEvent(HotkeyEventType Type, DateTimeOffset Timestamp, TimeSpan? HoldDuration = null, bool IsHold = false);

public enum HotkeyHoldPhase
{
    Start = 0,
    End = 1
}

public sealed class HotkeyHoldEventArgs : EventArgs
{
    public HotkeyEventType Type { get; }
    public HotkeyHoldPhase Phase { get; }
    /// <summary>String representation for compatibility with spec snippet (Start/End).</summary>
    public string PhaseString => Phase.ToString();
    public TimeSpan HoldDuration { get; }
    public DateTimeOffset Timestamp { get; }
    public int HotkeyId { get; }

    public HotkeyHoldEventArgs(HotkeyEventType type, HotkeyHoldPhase phase, TimeSpan holdDuration, DateTimeOffset timestamp, int hotkeyId)
    {
        Type = type;
        Phase = phase;
        HoldDuration = holdDuration;
        Timestamp = timestamp;
        HotkeyId = hotkeyId;
    }
}

public enum KeyStatePhase
{
    Down = 0,
    Up = 1
}

public sealed class KeyStateChangedEventArgs : EventArgs
{
    public int Id { get; }
    public KeyStatePhase Phase { get; }
    public TimeSpan? HoldDuration { get; }
    public DateTimeOffset Timestamp { get; }

    public KeyStateChangedEventArgs(int id, KeyStatePhase phase, TimeSpan? holdDuration, DateTimeOffset timestamp)
    {
        Id = id;
        Phase = phase;
        HoldDuration = holdDuration;
        Timestamp = timestamp;
    }
}
