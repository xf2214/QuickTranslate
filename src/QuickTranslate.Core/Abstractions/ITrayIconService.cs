namespace QuickTranslate.Core.Abstractions;

public interface ITrayIconService
{
    void Show();

    void Hide();

    void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info);
}
