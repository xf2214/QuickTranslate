namespace QuickTranslate.Core.Options;

public class HotkeyCombo
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.None;
    public KeyboardKey Key { get; set; } = KeyboardKey.None;

    public HotkeyCombo() { }

    public HotkeyCombo(HotkeyModifiers modifiers, KeyboardKey key)
    {
        Modifiers = modifiers;
        Key = key;
    }
}

public enum TranslationQuality
{
    Fast,
    Balanced
}

public class AppSettings
{
    public HotkeyCombo WordHotkey { get; set; } = new(HotkeyModifiers.Alt, KeyboardKey.D1);
    public HotkeyCombo BlockHotkey { get; set; } = new(HotkeyModifiers.Alt, KeyboardKey.D2);
    public string TargetLanguage { get; set; } = "zh-CN";
    public TranslationQuality TranslationQuality { get; set; } = TranslationQuality.Fast;
    public bool StartWithWindows { get; set; } = false;
    public bool CloseOnOutsideClick { get; set; } = true;
    public bool DebugLogging { get; set; } = false;
}
