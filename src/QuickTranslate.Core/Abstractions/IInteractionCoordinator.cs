using QuickTranslate.Core.Options;

namespace QuickTranslate.Core.Abstractions;

public interface IInteractionCoordinator
{
    void HandleHotkey(HotkeyEvent hotkeyEvent);
    void ShowTranslationPopup(string sourceText, string translatedText);
    void ShowSettingsWindow();
}
