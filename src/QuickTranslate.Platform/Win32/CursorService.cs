using System.Runtime.InteropServices;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.Platform.Win32;

public sealed class CursorService : ICursorService
{
    public unsafe PhysicalPoint GetPhysicalCursorPos(out MonitorId monitorId)
    {
        POINT pt;
        if (!User32.GetCursorPos(&pt))
        {
            monitorId = MonitorId.Empty;
            return PhysicalPoint.Zero;
        }

        IntPtr hMonitor = User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTONEAREST);
        string deviceName = string.Empty;

        if (hMonitor != IntPtr.Zero)
        {
            MONITORINFOEXW mi = new();
            mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
            if (User32.GetMonitorInfoW(hMonitor, ref mi))
            {
                deviceName = mi.szDevice ?? string.Empty;
            }
        }

        monitorId = new MonitorId(hMonitor, deviceName);
        return new PhysicalPoint(pt.X, pt.Y);
    }
}
