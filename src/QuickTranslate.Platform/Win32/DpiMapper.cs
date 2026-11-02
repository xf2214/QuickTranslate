using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Platform.Win32;

public sealed class DpiMapper : IDpiMapper
{
    public DipPoint ToDip(PhysicalPoint pt, uint dpiX = 96, uint dpiY = 96)
    {
        double x = pt.X * 96.0 / (dpiX == 0 ? 96.0 : dpiX);
        double y = pt.Y * 96.0 / (dpiY == 0 ? 96.0 : dpiY);
        return new DipPoint(x, y);
    }

    public PhysicalPoint ToPhysical(DipPoint pt, uint dpiX = 96, uint dpiY = 96)
    {
        double sx = (dpiX == 0 ? 96.0 : dpiX) / 96.0;
        double sy = (dpiY == 0 ? 96.0 : dpiY) / 96.0;
        int x = (int)Math.Round(pt.X * sx, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(pt.Y * sy, MidpointRounding.AwayFromZero);
        return new PhysicalPoint(x, y);
    }

    public DipRect ToDip(PhysicalRect r, uint dpiX, uint dpiY)
    {
        double dx = dpiX == 0 ? 1.0 : 96.0 / dpiX;
        double dy = dpiY == 0 ? 1.0 : 96.0 / dpiY;
        double x = r.X * dx;
        double y = r.Y * dy;
        double w = r.Width * dx;
        double h = r.Height * dy;
        return new DipRect(x, y, w, h);
    }

    public PhysicalRect ToPhysical(DipRect r, uint dpiX, uint dpiY)
    {
        double sx = (dpiX == 0 ? 96.0 : dpiX) / 96.0;
        double sy = (dpiY == 0 ? 96.0 : dpiY) / 96.0;
        int x = (int)Math.Round(r.X * sx, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(r.Y * sy, MidpointRounding.AwayFromZero);
        int w = (int)Math.Round(r.Width * sx, MidpointRounding.AwayFromZero);
        int h = (int)Math.Round(r.Height * sy, MidpointRounding.AwayFromZero);
        return new PhysicalRect(x, y, w, h);
    }

    /// <summary>
    /// 多屏物理坐标转 DIP：提供 Monitor 物理边界时，按目标屏物理原点计算相对偏移：
    /// DipX = Monitor.DipLeft + (PhysicalX - Monitor.PhysicalLeft) * (96.0 / Monitor.DpiX)
    /// </summary>
    public DipPoint ToDip(
        PhysicalPoint pt,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        double dx = dpiX == 0 ? 1.0 : 96.0 / dpiX;
        double dy = dpiY == 0 ? 1.0 : 96.0 / dpiY;

        double x, y;
        if (monitorPhysicalBounds.HasValue)
        {
            var mon = monitorPhysicalBounds.Value;
            double dipLeft = monitorDipLeft ?? mon.X;
            double dipTop = monitorDipTop ?? mon.Y;
            x = dipLeft + (pt.X - mon.X) * dx;
            y = dipTop + (pt.Y - mon.Y) * dy;
        }
        else
        {
            x = pt.X * dx;
            y = pt.Y * dy;
        }

        return new DipPoint(x, y);
    }

    public DipPoint ToDip(
        PhysicalPoint pt,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToDip(pt, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }

    /// <summary>
    /// 多屏物理矩形转 DIP：提供 Monitor 物理边界时，按目标屏物理原点计算相对偏移与尺寸。
    /// </summary>
    public DipRect ToDip(
        PhysicalRect r,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        double dx = dpiX == 0 ? 1.0 : 96.0 / dpiX;
        double dy = dpiY == 0 ? 1.0 : 96.0 / dpiY;

        double x, y;
        if (monitorPhysicalBounds.HasValue)
        {
            var mon = monitorPhysicalBounds.Value;
            double dipLeft = monitorDipLeft ?? mon.X;
            double dipTop = monitorDipTop ?? mon.Y;
            x = dipLeft + (r.X - mon.X) * dx;
            y = dipTop + (r.Y - mon.Y) * dy;
        }
        else
        {
            x = r.X * dx;
            y = r.Y * dy;
        }

        double w = r.Width * dx;
        double h = r.Height * dy;
        return new DipRect(x, y, w, h);
    }

    public DipRect ToDip(
        PhysicalRect r,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToDip(r, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }

    /// <summary>
    /// DIP 点转物理坐标：提供 Monitor 物理边界时，按目标屏物理原点与 DIP 原点还原全局物理坐标。
    /// </summary>
    public PhysicalPoint ToPhysical(
        DipPoint pt,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        double sx = (dpiX == 0 ? 96.0 : dpiX) / 96.0;
        double sy = (dpiY == 0 ? 96.0 : dpiY) / 96.0;

        int x, y;
        if (monitorPhysicalBounds.HasValue)
        {
            var mon = monitorPhysicalBounds.Value;
            double dipLeft = monitorDipLeft ?? mon.X;
            double dipTop = monitorDipTop ?? mon.Y;
            x = mon.X + (int)Math.Round((pt.X - dipLeft) * sx, MidpointRounding.AwayFromZero);
            y = mon.Y + (int)Math.Round((pt.Y - dipTop) * sy, MidpointRounding.AwayFromZero);
        }
        else
        {
            x = (int)Math.Round(pt.X * sx, MidpointRounding.AwayFromZero);
            y = (int)Math.Round(pt.Y * sy, MidpointRounding.AwayFromZero);
        }

        return new PhysicalPoint(x, y);
    }

    public PhysicalPoint ToPhysical(
        DipPoint pt,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToPhysical(pt, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }

    /// <summary>
    /// DIP 矩形转物理坐标：提供 Monitor 物理边界时，按目标屏物理原点与 DIP 原点还原全局物理矩形。
    /// </summary>
    public PhysicalRect ToPhysical(
        DipRect r,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        double sx = (dpiX == 0 ? 96.0 : dpiX) / 96.0;
        double sy = (dpiY == 0 ? 96.0 : dpiY) / 96.0;

        int x, y;
        if (monitorPhysicalBounds.HasValue)
        {
            var mon = monitorPhysicalBounds.Value;
            double dipLeft = monitorDipLeft ?? mon.X;
            double dipTop = monitorDipTop ?? mon.Y;
            x = mon.X + (int)Math.Round((r.X - dipLeft) * sx, MidpointRounding.AwayFromZero);
            y = mon.Y + (int)Math.Round((r.Y - dipTop) * sy, MidpointRounding.AwayFromZero);
        }
        else
        {
            x = (int)Math.Round(r.X * sx, MidpointRounding.AwayFromZero);
            y = (int)Math.Round(r.Y * sy, MidpointRounding.AwayFromZero);
        }

        int w = (int)Math.Round(r.Width * sx, MidpointRounding.AwayFromZero);
        int h = (int)Math.Round(r.Height * sy, MidpointRounding.AwayFromZero);
        return new PhysicalRect(x, y, w, h);
    }

    public PhysicalRect ToPhysical(
        DipRect r,
        MonitorInfo monitor,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ToPhysical(r, monitor.DpiX, monitor.DpiY, monitor.Bounds, monitorDipLeft, monitorDipTop);
    }
}
