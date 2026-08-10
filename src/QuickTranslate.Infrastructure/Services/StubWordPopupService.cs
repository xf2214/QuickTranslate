using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Services;

public class StubWordPopupService : IWordPopupService
{
    public int ShowCount { get; private set; }
    public int HideCount { get; private set; }
    public int HideAllCount { get; private set; }

    public void Show(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX = 96, uint dpiY = 96)
    {
        ShowCount++;
    }

    public void Hide()
    {
        HideCount++;
    }

    public void HideAll()
    {
        HideAllCount++;
    }
}
