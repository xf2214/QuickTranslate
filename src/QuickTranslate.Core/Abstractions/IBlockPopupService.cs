using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Abstractions;

public interface IBlockPopupService
{
    void HideAll();
    void Show(BlockSelectionResult blockSelection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY);
    void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId);
}
