using System.Windows.Input;
using QuickTranslate.Core.Options;

namespace QuickTranslate.Platform.Hotkeys;

public static class WpfHotkeyMapper
{
    public static ModifierKeys ToWpf(HotkeyModifiers modifiers)
    {
        var result = ModifierKeys.None;
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
            result |= ModifierKeys.Alt;
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl))
            result |= ModifierKeys.Control;
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
            result |= ModifierKeys.Shift;
        if (modifiers.HasFlag(HotkeyModifiers.Win))
            result |= ModifierKeys.Windows;
        return result;
    }

    public static HotkeyModifiers FromWpf(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control))
            result |= HotkeyModifiers.Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            result |= HotkeyModifiers.Win;
        return result;
    }

    public static Key ToWpf(KeyboardKey key)
    {
        if (key == KeyboardKey.None)
            return Key.None;
        return KeyInterop.KeyFromVirtualKey((int)key);
    }

    public static KeyboardKey FromWpf(Key key)
    {
        if (key == Key.None)
            return KeyboardKey.None;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (Enum.IsDefined(typeof(KeyboardKey), vk))
            return (KeyboardKey)vk;
        return KeyboardKey.None;
    }
}
