using System.Windows.Input;

namespace QuickTranslate.Core.Options;

public class HotkeyCombo
{
    public ModifierKeys Modifiers { get; set; } = ModifierKeys.None;
    public Key Key { get; set; } = Key.None;

    public HotkeyCombo() { }

    public HotkeyCombo(ModifierKeys modifiers, Key key)
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
    public HotkeyCombo WordHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, Key.W);
    public HotkeyCombo BlockHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, Key.B);
    public string TargetLanguage { get; set; } = "zh-CN";
    public TranslationQuality TranslationQuality { get; set; } = TranslationQuality.Balanced;
    public bool StartWithWindows { get; set; } = false;
    public bool CloseOnOutsideClick { get; set; } = true;
    public bool DebugLogging { get; set; } = false;
}
