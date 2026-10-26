using QuickTranslate.Core.Abstractions;

namespace QuickTranslate.Platform.Win32;

/// <summary>Relays Escape events from the shared keyboard hook to the block coordinator.</summary>
public sealed class DefaultEscHook : IEscHook
{
    public event EventHandler? EscPressed;

    public void RaiseEscPressed() => EscPressed?.Invoke(this, EventArgs.Empty);
}
