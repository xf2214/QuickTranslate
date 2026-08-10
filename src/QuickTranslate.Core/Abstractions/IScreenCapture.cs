using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

public interface IScreenCapture
{
    Task<ScreenFrame> CaptureAroundAsync(PhysicalPoint anchor, PhysicalSize size, CancellationToken ct = default);
    Task<ScreenFrame> CaptureRectAsync(PhysicalRect region, MonitorId? monitorHint = null, CancellationToken ct = default);
}
