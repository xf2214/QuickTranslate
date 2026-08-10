using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Abstractions;

public interface IWordPopupService
{
    void Show(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX = 96, uint dpiY = 96);

    void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId);

    void Hide();

    void HideAll();
}
