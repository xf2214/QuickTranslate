using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

public interface ISelectionOverlayService
{
    /// <summary>preview=true 时以虚线样式展示（截图范围预览框），false 为实线选定框。</summary>
    void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96, bool preview = false);
    void Update(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96);
    void Hide(MonitorId monitorId);
    void HideAll();
    IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect();
}
