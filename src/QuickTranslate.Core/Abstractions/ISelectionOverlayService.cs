using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

public interface ISelectionOverlayService
{
    void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96);
    void Update(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96);
    void Hide(MonitorId monitorId);
    void HideAll();
    IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect();
}
