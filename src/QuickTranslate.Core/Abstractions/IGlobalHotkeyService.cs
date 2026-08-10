using QuickTranslate.Core.Options;

namespace QuickTranslate.Core.Abstractions;

public sealed class HotkeyEventArgs : EventArgs
{
    public int Id { get; }
    public HotkeyModifiers Modifiers { get; }
    public KeyboardKey Key { get; }

    public HotkeyEventArgs(int id, HotkeyModifiers modifiers, KeyboardKey key)
    {
        Id = id;
        Modifiers = modifiers;
        Key = key;
    }
}

public interface IGlobalHotkeyService
{
    bool Register(int id, HotkeyModifiers modifiers, KeyboardKey key);
    void Unregister(int id);
    void UnregisterAll();

    event EventHandler<HotkeyEventArgs>? HotkeyPressed;
}
