using System.Windows.Interop;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.App.Windows;
using QuickTranslate.Platform.UnmanagedMethods;
using QuickTranslate.Platform.Win32;
using QuickTranslate.App.Coordination;

namespace QuickTranslate.App.Services;

public class WpfSelectionOverlayService : ISelectionOverlayService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)> _windows = new();

    public WpfSelectionOverlayService(IDpiMapper dpiMapper)
    {
        _dpiMapper = dpiMapper;
    }

    public void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96)
    {
        bool needsRecreate = true;
        if (_windows.TryGetValue(monitorId, out var entry))
        {
            if (PerMonitorDpiHelpers.AreClose(entry.dpiX, dpiX) && PerMonitorDpiHelpers.AreClose(entry.dpiY, dpiY))
            {
                needsRecreate = false;
            }
            else
            {
                entry.window.Close();
                _windows.Remove(monitorId);
            }
        }

        SelectionOverlayWindow window;
        if (needsRecreate || !_windows.TryGetValue(monitorId, out entry))
        {
            window = new SelectionOverlayWindow();
            _windows[monitorId] = (window, dpiX, dpiY);
        }
        else
        {
            window = entry.window;
            _windows[monitorId] = (window, dpiX, dpiY);
        }

        window.ApplyPhysicalLayout(physicalBox, dpiX, dpiY);

        if (!window.IsVisible)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                hwnd = new WindowInteropHelper(window).EnsureHandle();
            }
            window.Show();
        }
    }

    public void Update(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96)
    {
        if (_windows.TryGetValue(monitorId, out var entry))
        {
            entry.window.ApplyPhysicalLayout(physicalBox, dpiX, dpiY);
            _windows[monitorId] = (entry.window, dpiX, dpiY);

            IntPtr hwnd = new WindowInteropHelper(entry.window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                User32.SetWindowPos(
                    hwnd,
                    User32.HWND_TOPMOST,
                    0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            }
        }
    }

    public void Hide(MonitorId monitorId)
    {
        if (_windows.TryGetValue(monitorId, out var entry))
        {
            entry.window.Hide();
        }
    }

    public void HideAll()
    {
        foreach (var kvp in _windows)
        {
            kvp.Value.window.Hide();
        }
    }

    public IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect()
    {
        var result = new Dictionary<MonitorId, (IntPtr, DipRect)>();
        foreach (var kvp in _windows)
        {
            IntPtr hwnd = new WindowInteropHelper(kvp.Value.window).Handle;
            result[kvp.Key] = (hwnd, kvp.Value.window.LastLayoutRect);
        }
        return result;
    }
}
