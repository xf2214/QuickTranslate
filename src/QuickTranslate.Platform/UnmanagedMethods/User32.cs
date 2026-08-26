using System.Runtime.InteropServices;

namespace QuickTranslate.Platform.UnmanagedMethods;

public static unsafe partial class User32
{
    private const string DllName = "user32.dll";

    [LibraryImport(DllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(POINT* lpPoint);

    [LibraryImport(DllName)]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport(DllName)]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public unsafe delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, RECT* lprcMonitor, IntPtr dwData);

    [LibraryImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport(DllName, EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    [LibraryImport(DllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport(DllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    public const int GWL_EXSTYLE = -20;

    [LibraryImport(DllName, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [LibraryImport(DllName, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWNA = 8;

    [LibraryImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [LibraryImport(DllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport(DllName)]
    public static partial IntPtr GetDesktopWindow();

    // GetDC/ReleaseDC 虽然名称含 "DC"，但 Windows 从 user32.dll 导出（非 gdi32.dll）。
    // 历史 Bug：声明在 Gdi32.cs（gdi32.dll）导致 EntryPointNotFoundException，屏幕截图崩溃，
    // 进而截断整个 OCR→选定框→翻译→弹窗管线。
    [LibraryImport(DllName)]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport(DllName)]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    // 低级鼠标钩子（WH_MOUSE_LL）——用于支持鼠标侧键作为热键
    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;

    public const int XBUTTON1 = 0x0001;
    public const int XBUTTON2 = 0x0002;

    [LibraryImport(DllName, SetLastError = true)]
    public static partial IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport(DllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport(DllName, SetLastError = true)]
    public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport(DllName, SetLastError = true)]
    public static partial short GetAsyncKeyState(int vKey);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport(DllName, SetLastError = true)]
    public static partial IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
}
