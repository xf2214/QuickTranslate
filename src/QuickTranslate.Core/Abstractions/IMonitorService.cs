using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> EnumerateMonitors();
    MonitorInfo? TryGetMonitorFromPoint(PhysicalPoint pt);
    MonitorInfo? TryGetPrimary();
}
