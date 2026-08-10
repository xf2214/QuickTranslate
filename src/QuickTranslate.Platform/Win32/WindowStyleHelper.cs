using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.Platform.Win32;

public static class WindowStyleHelper
{
    public static IntPtr GetExtendedStyle(IntPtr hwnd)
    {
        return User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
    }

    public static void SetNoActivateToolTopMost(IntPtr hwnd)
    {
        IntPtr currentStyle = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
        UIntPtr style = unchecked((UIntPtr)(ulong)currentStyle.ToInt64());
        ulong newStyle = style.ToUInt64()
            | User32.WS_EX_NOACTIVATE
            | User32.WS_EX_TOOLWINDOW
            | User32.WS_EX_TOPMOST;

        User32.SetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE, unchecked((IntPtr)(long)newStyle));

        User32.SetWindowPos(
            hwnd,
            User32.HWND_TOPMOST,
            0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    public static void ShowWindowNoActivate(IntPtr hwnd)
    {
        User32.ShowWindow(hwnd, User32.SW_SHOWNA);
    }

    public static void ApplyNoActivate(IntPtr hwnd)
    {
        IntPtr currentStyle = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
        UIntPtr style = unchecked((UIntPtr)(ulong)currentStyle.ToInt64());
        ulong newStyle = style.ToUInt64() | User32.WS_EX_NOACTIVATE;
        User32.SetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE, unchecked((IntPtr)(long)newStyle));
    }

    public static bool SetForegroundWindow(IntPtr hwnd)
    {
        return User32.SetForegroundWindow(hwnd);
    }
}
