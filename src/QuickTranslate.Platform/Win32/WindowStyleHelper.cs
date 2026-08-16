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

    /// <summary>
    /// 置顶 + 不抢焦点 + 鼠标点击完全穿透（WS_EX_TRANSPARENT）。
    /// 用于纯展示型浮层（识别中指示器），生命周期内不接收任何输入。
    /// </summary>
    public static void SetTopMostNoActivateClickThrough(IntPtr hwnd)
    {
        IntPtr currentStyle = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
        ulong newStyle = unchecked((ulong)currentStyle.ToInt64())
            | User32.WS_EX_NOACTIVATE
            | User32.WS_EX_TOOLWINDOW
            | User32.WS_EX_TOPMOST
            | User32.WS_EX_TRANSPARENT;

        User32.SetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE, unchecked((IntPtr)(long)newStyle));

        User32.SetWindowPos(
            hwnd,
            User32.HWND_TOPMOST,
            0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
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
