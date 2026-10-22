using QuickTranslate.Core.Options;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.Platform.Win32;

/// <summary>Emits one cancellation per Escape press, ignoring keyboard auto-repeat.</summary>
public sealed class EscapeKeyState
{
    private bool _isDown;

    public bool ShouldEmit(int hookCode, int message, int virtualKey)
    {
        if (hookCode < 0 || virtualKey != (int)KeyboardKey.Escape)
            return false;

        if (message is User32.WM_KEYUP or User32.WM_SYSKEYUP)
        {
            _isDown = false;
            return false;
        }

        if (message is not (User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN) || _isDown)
            return false;

        _isDown = true;
        return true;
    }

    public void Reset() => _isDown = false;
}
