using System.Windows;
using System.Windows.Interop;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.Platform.Win32;

public sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private const int MessageHandled = 0;

    private readonly HwndSource _source;
    private readonly Dictionary<int, (HotkeyModifiers Modifiers, KeyboardKey Key)> _registered = new();
    private bool _disposed;

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

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
            uint vk = (uint)((lParam.ToInt64() >> 16) & 0xFFFF);

            if (_registered.TryGetValue(id, out var info))
            {
                HotkeyModifiers mods = HotkeyModifiers.None;
                if ((modifiers & User32.MOD_ALT) != 0) mods |= HotkeyModifiers.Alt;
                if ((modifiers & User32.MOD_CONTROL) != 0) mods |= HotkeyModifiers.Ctrl;
                if ((modifiers & User32.MOD_SHIFT) != 0) mods |= HotkeyModifiers.Shift;
                if ((modifiers & User32.MOD_WIN) != 0) mods |= HotkeyModifiers.Win;

                KeyboardKey key = Enum.IsDefined(typeof(KeyboardKey), vk) ? (KeyboardKey)vk : KeyboardKey.None;

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
        }

        return ok;
    }

    public void Unregister(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registered.Remove(id))
        {
            User32.UnregisterHotKey(_source.Handle, id);
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _source.Dispose();
    }
}
