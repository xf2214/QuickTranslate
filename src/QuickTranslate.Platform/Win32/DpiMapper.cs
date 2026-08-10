using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Platform.Win32;

public sealed class DpiMapper : IDpiMapper
{
    public DipPoint ToDip(PhysicalPoint pt, uint dpiX = 96, uint dpiY = 96)
    {
        double x = pt.X * 96.0 / dpiX;
        double y = pt.Y * 96.0 / dpiY;
        return new DipPoint(x, y);
    }

    public PhysicalPoint ToPhysical(DipPoint pt, uint dpiX = 96, uint dpiY = 96)
    {
        int x = (int)Math.Round(pt.X * dpiX / 96.0, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(pt.Y * dpiY / 96.0, MidpointRounding.AwayFromZero);
        return new PhysicalPoint(x, y);
    }

    public DipRect ToDip(PhysicalRect r, uint dpiX, uint dpiY)
    {
        double x = r.X * 96.0 / dpiX;
        double y = r.Y * 96.0 / dpiY;
        double w = r.Width * 96.0 / dpiX;
        double h = r.Height * 96.0 / dpiY;
        return new DipRect(x, y, w, h);
    }

    public PhysicalRect ToPhysical(DipRect r, uint dpiX, uint dpiY)
    {
        int x = (int)Math.Round(r.X * dpiX / 96.0, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(r.Y * dpiY / 96.0, MidpointRounding.AwayFromZero);
        int w = (int)Math.Round(r.Width * dpiX / 96.0, MidpointRounding.AwayFromZero);
        int h = (int)Math.Round(r.Height * dpiY / 96.0, MidpointRounding.AwayFromZero);
        return new PhysicalRect(x, y, w, h);
    }
}
