using System.Windows;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.App.Coordination;

public static class PerMonitorDpiHelpers
{
    public static (double left, double top, double width, double height) ToDip(PhysicalRect box, uint dpiX, uint dpiY)
    {
        double dx = dpiX == 0 ? 96.0 : 96.0 / dpiX;
        double dy = dpiY == 0 ? 96.0 : 96.0 / dpiY;
        return (box.X * dx, box.Y * dy, box.Width * dx, box.Height * dy);
    }

    public static Rect ToWpfRect(PhysicalRect box, uint dpiX, uint dpiY)
    {
        var (l, t, w, h) = ToDip(box, dpiX, dpiY);
        return new Rect(l, t, w, h);
    }

    public static bool AreClose(uint a, uint b) => Math.Abs((int)a - (int)b) <= 2;
}
