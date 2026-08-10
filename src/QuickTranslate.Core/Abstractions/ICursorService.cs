using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

public interface ICursorService
{
    PhysicalPoint GetPhysicalCursorPos(out MonitorId monitorId);
}
