using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;

namespace QuickTranslate.App.Validation;

public static class SettingsValidator
{
    public static ValidationResult Validate(
        HotkeyCombo wordHotkey,
        HotkeyCombo blockHotkey,
        IHotkeyBroker hotkeyBroker)
    {
        if (wordHotkey.Key == KeyboardKey.None)
            return ValidationResult.Fail("Word 热键无效");
        if (blockHotkey.Key == KeyboardKey.None)
            return ValidationResult.Fail("Block 热键无效");

        if (wordHotkey.Modifiers == blockHotkey.Modifiers && wordHotkey.Key == blockHotkey.Key)
            return ValidationResult.Fail("Word 与 Block 热键相同，发生冲突");

        if (!hotkeyBroker.Probe(wordHotkey.Modifiers, wordHotkey.Key))
            return ValidationResult.Fail("Word 热键与其他应用冲突，请更换键位");

        if (!hotkeyBroker.Probe(blockHotkey.Modifiers, blockHotkey.Key))
            return ValidationResult.Fail("Block 热键与其他应用冲突，请更换键位");

        return ValidationResult.Ok();
    }
}

public class ValidationResult
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    private ValidationResult(bool success, string? error)
    {
        IsSuccess = success;
        ErrorMessage = error;
    }

    public static ValidationResult Ok() => new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}
