namespace QuickTranslate.Core.Geometry;

public record MonitorInfo(
    MonitorId Id,
    string DeviceName,
    PhysicalRect Bounds,
    PhysicalRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary);
