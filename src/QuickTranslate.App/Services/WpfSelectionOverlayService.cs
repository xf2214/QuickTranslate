using System.Windows.Interop;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.App.Windows;
using QuickTranslate.Platform.UnmanagedMethods;
using QuickTranslate.Platform.Win32;

namespace QuickTranslate.App.Services;

public class WpfSelectionOverlayService : ISelectionOverlayService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly Dictionary<MonitorId, SelectionOverlayWindow> _windows = new();

    public WpfSelectionOverlayService(IDpiMapper dpiMapper)
    {
        _dpiMapper = dpiMapper;
    }

    public void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96)
    {
        if (!_windows.TryGetValue(monitorId, out var window))
        {
            window = new SelectionOverlayWindow();
            _windows[monitorId] = window;
        }

        DipRect dipRect = _dpiMapper.ToDip(physicalBox, dpiX, dpiY);
        window.ApplyLayout(dipRect);

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
        if (_windows.TryGetValue(monitorId, out var window))
        {
            DipRect dipRect = _dpiMapper.ToDip(physicalBox, dpiX, dpiY);
            window.ApplyLayout(dipRect);

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
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
        if (_windows.TryGetValue(monitorId, out var window))
        {
            window.Hide();
        }
    }

    public void HideAll()
    {
        foreach (var window in _windows.Values)
        {
            window.Hide();
        }
    }

    public IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect()
    {
        var result = new Dictionary<MonitorId, (IntPtr, DipRect)>();
        foreach (var kvp in _windows)
        {
            IntPtr hwnd = new WindowInteropHelper(kvp.Value).Handle;
            result[kvp.Key] = (hwnd, kvp.Value.LastLayoutRect);
        }
        return result;
    }
}
