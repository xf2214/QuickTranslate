using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

public interface IDpiMapper
{
    DipPoint ToDip(PhysicalPoint pt, uint dpiX = 96, uint dpiY = 96);
    PhysicalPoint ToPhysical(DipPoint pt, uint dpiX = 96, uint dpiY = 96);
    DipRect ToDip(PhysicalRect r, uint dpiX, uint dpiY);
    PhysicalRect ToPhysical(DipRect r, uint dpiX, uint dpiY);
}
