using System.Runtime.InteropServices;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.Platform.Win32;

public sealed class MonitorService : IMonitorService
{
    public unsafe IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var result = new List<MonitorInfo>();

        var listHandle = GCHandle.Alloc(result);
        try
        {
            IntPtr data = GCHandle.ToIntPtr(listHandle);

            User32.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, RECT* lprcMonitor, IntPtr dwData) =>
            {
                var list = (List<MonitorInfo>?)GCHandle.FromIntPtr(dwData).Target;
                if (list == null) return true;

                var info = GetMonitorInfoInternal(hMonitor);
                if (info != null) list.Add(info);
                return true;
            };

            User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, data);
        }
        finally
        {
            listHandle.Free();
        }

        return result;
    }

    public MonitorInfo? TryGetMonitorFromPoint(PhysicalPoint pt)
    {
        POINT nativePt = new() { X = pt.X, Y = pt.Y };
        IntPtr hMonitor = User32.MonitorFromPoint(nativePt, User32.MONITOR_DEFAULTTONULL);
        if (hMonitor == IntPtr.Zero) return null;
        return GetMonitorInfoInternal(hMonitor);
    }

    public MonitorInfo? TryGetPrimary()
    {
        foreach (var monitor in EnumerateMonitors())
        {
            if (monitor.IsPrimary) return monitor;
        }
        return null;
    }

    public MonitorId MonitorFromWindow(IntPtr hwnd)
    {
        IntPtr hMonitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return MonitorId.Empty;

        MONITORINFOEXW mi = new();
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        if (!User32.GetMonitorInfoW(hMonitor, ref mi))
        {
            return new MonitorId(hMonitor, string.Empty);
        }

        string deviceName = mi.szDevice ?? string.Empty;
        return new MonitorId(hMonitor, deviceName);
    }

    private MonitorInfo? GetMonitorInfoInternal(IntPtr hMonitor)
    {
        MONITORINFOEXW mi = new();
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        if (!User32.GetMonitorInfoW(hMonitor, ref mi))
            return null;

        uint dpiX = 96;
        uint dpiY = 96;
        int hr = Shcore.GetDpiForMonitor(hMonitor, Shcore.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
        if (hr != Shcore.S_OK)
        {
            dpiX = 96;
            dpiY = 96;
        }

        PhysicalRect bounds = new(
            mi.rcMonitor.Left,
            mi.rcMonitor.Top,
            mi.rcMonitor.Right - mi.rcMonitor.Left,
            mi.rcMonitor.Bottom - mi.rcMonitor.Top);

        PhysicalRect workArea = new(
            mi.rcWork.Left,
            mi.rcWork.Top,
            mi.rcWork.Right - mi.rcWork.Left,
            mi.rcWork.Bottom - mi.rcWork.Top);

        bool isPrimary = (mi.dwFlags & User32.MONITORINFOF_PRIMARY) != 0;
        string deviceName = mi.szDevice ?? string.Empty;

        return new MonitorInfo(
            new MonitorId(hMonitor, deviceName),
            deviceName,
            bounds,
            workArea,
            dpiX,
            dpiY,
            isPrimary);
    }
}
