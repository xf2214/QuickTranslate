namespace QuickTranslate.Core.Abstractions;

public interface IInteractionCoordinator
{
    void ShowTranslationPopup(string sourceText, string translatedText);
    void ShowSettingsWindow();
}
