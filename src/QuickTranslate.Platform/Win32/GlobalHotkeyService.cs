using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.Platform.Win32;

public sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private const int MessageHandled = 0;
    private const int HC_ACTION = 0;

    private readonly HwndSource _source;
    private readonly Dictionary<int, (HotkeyModifiers Modifiers, KeyboardKey Key)> _registered = new();
    private bool _disposed;

    // 鼠标按钮热键：RegisterHotKey 不支持鼠标按键（VK 0x01-0x06），需用低级鼠标钩子
    private readonly Dictionary<int, (HotkeyModifiers Modifiers, KeyboardKey Key)> _mouseButtonHotkeys = new();
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private User32.LowLevelMouseProc? _mouseHookProc;

    // 键盘长按检测：WH_KEYBOARD_LL 钩子
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private User32.LowLevelKeyboardProc? _keyboardHookProc;
    private readonly Dictionary<int, DateTime> _downTimes = new();

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;
    public event EventHandler<KeyStateChangedEventArgs>? KeyStateChanged;

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("QuickTranslate.GlobalHotkeyWindow")
        {
            ParentWindow = IntPtr.Zero,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            HwndSourceHook = WndProc,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0
        };

        _source = new HwndSource(parameters);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY)
        {
            int id = (int)wParam.ToInt64();
            uint modifiers = (uint)(lParam.ToInt64() & 0xFFFF);
            int vk = (int)((lParam.ToInt64() >> 16) & 0xFFFF);

            if (_registered.TryGetValue(id, out var info))
            {
                HotkeyModifiers mods = HotkeyModifiers.None;
                if ((modifiers & User32.MOD_ALT) != 0) mods |= HotkeyModifiers.Alt;
                if ((modifiers & User32.MOD_CONTROL) != 0) mods |= HotkeyModifiers.Ctrl;
                if ((modifiers & User32.MOD_SHIFT) != 0) mods |= HotkeyModifiers.Shift;
                if ((modifiers & User32.MOD_WIN) != 0) mods |= HotkeyModifiers.Win;

                // 按键合法性判断：任何情况下都不得抛异常。曾有构建把 vk 误当 uint，
                // 导致 Enum.IsDefined 抛 ArgumentException，热键在派发前就被中断（按了没反应）。
                KeyboardKey key = KeyboardKey.None;
                try
                {
                    key = vk >= 0 && Enum.IsDefined(typeof(KeyboardKey), vk)
                        ? (KeyboardKey)vk
                        : KeyboardKey.None;
                }
                catch (ArgumentException)
                {
                    key = KeyboardKey.None;
                }

                var args = new HotkeyEventArgs(id, mods, key);

                if (Application.Current?.Dispatcher != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(() => HotkeyPressed?.Invoke(this, args));
                }
                else
                {
                    HotkeyPressed?.Invoke(this, args);
                }
            }

            handled = true;
            return (IntPtr)MessageHandled;
        }

        return IntPtr.Zero;
    }

    public bool Register(int id, HotkeyModifiers modifiers, KeyboardKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 鼠标按键走低级鼠标钩子，RegisterHotKey 不支持
        if (IsMouseButton(key))
        {
            return RegisterMouseButtonHotkey(id, modifiers, key);
        }

        uint fsModifiers = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) fsModifiers |= User32.MOD_ALT;
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) fsModifiers |= User32.MOD_CONTROL;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) fsModifiers |= User32.MOD_SHIFT;
        if (modifiers.HasFlag(HotkeyModifiers.Win)) fsModifiers |= User32.MOD_WIN;
        fsModifiers |= User32.MOD_NOREPEAT;

        uint vk = (uint)key;

        if (_registered.ContainsKey(id))
        {
            User32.UnregisterHotKey(_source.Handle, id);
            _registered.Remove(id);
        }

        bool ok = User32.RegisterHotKey(_source.Handle, id, fsModifiers, vk);
        if (ok)
        {
            _registered[id] = (modifiers, key);
            EnsureKeyboardHookInstalled();
        }

        return ok;
    }

    private bool RegisterMouseButtonHotkey(int id, HotkeyModifiers modifiers, KeyboardKey key)
    {
        // 移除旧的鼠标按键注册
        _mouseButtonHotkeys.Remove(id);

        _mouseButtonHotkeys[id] = (modifiers, key);

        // 确保钩子已安装
        EnsureMouseHookInstalled();

        return true;
    }

    private void EnsureMouseHookInstalled()
    {
        if (_mouseHookHandle != IntPtr.Zero)
            return;

        _mouseHookProc = MouseHookCallback;
        IntPtr hMod = Kernel32.GetModuleHandleW(null);
        _mouseHookHandle = User32.SetWindowsHookExW(User32.WH_MOUSE_LL, _mouseHookProc, hMod, 0);
    }

    private void EnsureKeyboardHookInstalled()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
            return;

        _keyboardHookProc = KeyboardHookCallback;
        IntPtr hMod = Kernel32.GetModuleHandleW(null);
        _keyboardHookHandle = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, _keyboardHookProc, hMod, 0);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION && _registered.Count > 0)
        {
            int msg = (int)wParam.ToInt64();
            bool isDown = msg == User32.WM_KEYDOWN || msg == User32.WM_SYSKEYDOWN;
            bool isUp = msg == User32.WM_KEYUP || msg == User32.WM_SYSKEYUP;
            if (isDown || isUp)
            {
                int vk = Marshal.ReadInt32(lParam);
                foreach (var kvp in _registered.ToArray())
                {
                    int id = kvp.Key;
                    var (mods, key) = kvp.Value;
                    if ((int)key != vk)
                        continue;
                    if (!CheckModifiers(mods))
                        continue;

                    if (isDown)
                    {
                        if (_downTimes.ContainsKey(id))
                            continue; // ignore auto-repeat
                        _downTimes[id] = DateTime.UtcNow;
                        var args = new KeyStateChangedEventArgs(id, KeyStatePhase.Down, null, DateTimeOffset.UtcNow);
                        RaiseKeyStateChanged(args);
                    }
                    else if (isUp)
                    {
                        if (!_downTimes.TryGetValue(id, out var downTime))
                            continue;
                        _downTimes.Remove(id);
                        var duration = DateTime.UtcNow - downTime;
                        var args = new KeyStateChangedEventArgs(id, KeyStatePhase.Up, duration, DateTimeOffset.UtcNow);
                        RaiseKeyStateChanged(args);
                    }
                }
            }
        }

        return User32.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private void RaiseKeyStateChanged(KeyStateChangedEventArgs args)
    {
        // 长按 400ms 判定对时序敏感：BeginInvoke 异步会导致 Down/Up 在 UI 线程的
        // 队列中乱序，短按也可能在 400ms 后才处理 Down 而误触发 HoldStart。
        // 改为同步 Invoke 确保 Down/Up 顺序与物理按键一致，Hold 计时才能准确区分点按与长按。
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => KeyStateChanged?.Invoke(this, args));
        }
        else
        {
            KeyStateChanged?.Invoke(this, args);
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION && _mouseButtonHotkeys.Count > 0)
        {
            int msg = (int)wParam;
            KeyboardKey? pressedButton = null;

            if (msg == User32.WM_XBUTTONDOWN)
            {
                // HIWORD of lParam contains the xbutton (1 or 2)
                int hiWord = Marshal.ReadInt32(lParam, 8); // HIWORD is at offset 8 (after POINT + mouseData low word)
                int xButton = (hiWord >> 16) & 0xFFFF;
                pressedButton = xButton == User32.XBUTTON1 ? KeyboardKey.XButton1 : KeyboardKey.XButton2;
            }
            else if (msg == User32.WM_LBUTTONDOWN)
            {
                pressedButton = KeyboardKey.LButton;
            }
            else if (msg == User32.WM_RBUTTONDOWN)
            {
                pressedButton = KeyboardKey.RButton;
            }
            else if (msg == User32.WM_MBUTTONDOWN)
            {
                pressedButton = KeyboardKey.MButton;
            }

            if (pressedButton != null)
            {
                foreach (var kvp in _mouseButtonHotkeys)
                {
                    if (kvp.Value.Key == pressedButton && CheckModifiers(kvp.Value.Modifiers))
                    {
                        var args = new HotkeyEventArgs(kvp.Key, kvp.Value.Modifiers, kvp.Value.Key);

                        if (Application.Current?.Dispatcher != null)
                        {
                            Application.Current.Dispatcher.BeginInvoke(() => HotkeyPressed?.Invoke(this, args));
                        }
                        else
                        {
                            HotkeyPressed?.Invoke(this, args);
                        }
                    }
                }
            }
        }

        return User32.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private static bool CheckModifiers(HotkeyModifiers required)
    {
        if (required == HotkeyModifiers.None)
            return true;

        bool alt = (User32.GetAsyncKeyState(0x12) & 0x8000) != 0;       // VK_MENU
        bool ctrl = (User32.GetAsyncKeyState(0x11) & 0x8000) != 0;      // VK_CONTROL
        bool shift = (User32.GetAsyncKeyState(0x10) & 0x8000) != 0;     // VK_SHIFT
        bool win = (User32.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (User32.GetAsyncKeyState(0x5C) & 0x8000) != 0; // VK_LWIN/VK_RWIN

        if (required.HasFlag(HotkeyModifiers.Alt) && !alt) return false;
        if (required.HasFlag(HotkeyModifiers.Ctrl) && !ctrl) return false;
        if (required.HasFlag(HotkeyModifiers.Shift) && !shift) return false;
        if (required.HasFlag(HotkeyModifiers.Win) && !win) return false;
        return true;
    }

    private static bool IsMouseButton(KeyboardKey key)
    {
        return key == KeyboardKey.LButton ||
               key == KeyboardKey.RButton ||
               key == KeyboardKey.MButton ||
               key == KeyboardKey.XButton1 ||
               key == KeyboardKey.XButton2;
    }

    public void Unregister(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registered.Remove(id))
        {
            User32.UnregisterHotKey(_source.Handle, id);
        }

        _mouseButtonHotkeys.Remove(id);
        _downTimes.Remove(id);

        // 没有鼠标按键热键时卸载钩子
        if (_mouseButtonHotkeys.Count == 0 && _mouseHookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            _mouseHookProc = null;
        }

        if (_registered.Count == 0 && _keyboardHookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
            _keyboardHookProc = null;
        }
    }

    public void UnregisterAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (int id in _registered.Keys)
        {
            User32.UnregisterHotKey(_source.Handle, id);
        }
        _registered.Clear();
        _mouseButtonHotkeys.Clear();
        _downTimes.Clear();

        if (_mouseHookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            _mouseHookProc = null;
        }

        if (_keyboardHookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
            _keyboardHookProc = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _source.Dispose();
    }
}