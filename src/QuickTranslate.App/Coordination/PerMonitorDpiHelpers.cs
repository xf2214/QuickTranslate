using System.Windows;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.App.Coordination;

public static class PerMonitorDpiHelpers
{
    /// <summary>
    /// 物理矩形转 DIP：在提供 Monitor 物理边界信息时，DIP 映射按目标屏物理原点计算相对偏移：
    /// DipX = Monitor.DipLeft + (PhysicalX - Monitor.PhysicalLeft) * (96.0 / Monitor.DpiX)
    /// DipY = Monitor.DipTop + (PhysicalY - Monitor.PhysicalTop) * (96.0 / Monitor.DpiY)
    /// 未提供 Monitor 物理边界时退回全局比例换算（主屏兼容模式）。
    /// </summary>
    public static (double left, double top, double width, double height) ToDip(
        PhysicalRect box,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds = null,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        double dx = dpiX == 0 ? 1.0 : 96.0 / dpiX;
        double dy = dpiY == 0 ? 1.0 : 96.0 / dpiY;

        double left, top;
        if (monitorPhysicalBounds.HasValue)
        {
            var mon = monitorPhysicalBounds.Value;
            double dipLeft = monitorDipLeft ?? mon.X;
            double dipTop = monitorDipTop ?? mon.Y;
            left = dipLeft + (box.X - mon.X) * dx;
            top = dipTop + (box.Y - mon.Y) * dy;
        }
        else
        {
            left = box.X * dx;
            top = box.Y * dy;
        }

        double width = box.Width * dx;
        double height = box.Height * dy;
        return (left, top, width, height);
    }

    public static (double left, double top, double width, double height) ToDip(
        PhysicalRect box,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToDip(box, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }

    public static (double x, double y) ToDip(
        PhysicalPoint pt,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds = null,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        double dx = dpiX == 0 ? 1.0 : 96.0 / dpiX;
        double dy = dpiY == 0 ? 1.0 : 96.0 / dpiY;

        if (monitorPhysicalBounds.HasValue)
        {
            var mon = monitorPhysicalBounds.Value;
            double dipLeft = monitorDipLeft ?? mon.X;
            double dipTop = monitorDipTop ?? mon.Y;
            return (dipLeft + (pt.X - mon.X) * dx, dipTop + (pt.Y - mon.Y) * dy);
        }

        return (pt.X * dx, pt.Y * dy);
    }

    public static (double x, double y) ToDip(
        PhysicalPoint pt,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToDip(pt, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }

    public static Rect ToWpfRect(
        PhysicalRect box,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds = null,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        var (l, t, w, h) = ToDip(box, dpiX, dpiY, monitorPhysicalBounds, monitorDipLeft, monitorDipTop);
        return new Rect(l, t, w, h);
    }

    public static Rect ToWpfRect(
        PhysicalRect box,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToWpfRect(box, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }

    public static System.Windows.Point ToWpfPoint(
        PhysicalPoint pt,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds = null,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        var (x, y) = ToDip(pt, dpiX, dpiY, monitorPhysicalBounds, monitorDipLeft, monitorDipTop);
        return new System.Windows.Point(x, y);
    }

    public static System.Windows.Point ToWpfPoint(
        PhysicalPoint pt,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToWpfPoint(pt, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }

    public static bool AreClose(uint a, uint b) => Math.Abs((int)a - (int)b) <= 2;
}

