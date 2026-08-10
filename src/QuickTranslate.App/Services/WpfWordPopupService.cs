using System.Windows.Interop;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.App.Services;

public class WpfWordPopupService : IWordPopupService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly IMonitorService _monitorService;
    private readonly Dictionary<MonitorId, WordPopupWindow> _windows = new();

    public WpfWordPopupService(IDpiMapper dpiMapper, IMonitorService monitorService)
    {
        _dpiMapper = dpiMapper;
        _monitorService = monitorService;
    }

    public void Show(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX = 96, uint dpiY = 96)
    {
        if (!_windows.TryGetValue(monitorId, out var window))
        {
            window = new WordPopupWindow();
            _windows[monitorId] = window;
        }

        var monitors = _monitorService.EnumerateMonitors();
        var monitorInfo = monitors.FirstOrDefault(m => m.Id == monitorId)
                         ?? _monitorService.TryGetPrimary()
                         ?? new MonitorInfo(monitorId, string.Empty,
                             new PhysicalRect(0, 0, 1920, 1080),
                             new PhysicalRect(0, 0, 1920, 1080),
                             dpiX, dpiY, true);

        int popupPhysicalW = (int)Math.Round(320.0 * dpiX / 96.0);
        int popupPhysicalH = (int)Math.Round(150.0 * dpiY / 96.0);
        var popupPreferredSize = new PhysicalSize(popupPhysicalW, popupPhysicalH);

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, popupPreferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        window.Left = dipRect.X;
        window.Top = dipRect.Y;
        window.Width = dipRect.Width;
        window.Height = Math.Max(dipRect.Height, 150);

        window.ApplyContent(selection, translation);
        window.SetLastLayoutDipRect(dipRect);

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

    public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
    {
        if (!_windows.TryGetValue(monitorId, out var window))
        {
            window = new WordPopupWindow();
            _windows[monitorId] = window;
        }

        var monitors = _monitorService.EnumerateMonitors();
        var monitorInfo = monitors.FirstOrDefault(m => m.Id == monitorId)
                         ?? _monitorService.TryGetPrimary()
                         ?? new MonitorInfo(monitorId, string.Empty,
                             new PhysicalRect(0, 0, 1920, 1080),
                             new PhysicalRect(0, 0, 1920, 1080),
                             dpiX, dpiY, true);

        int popupPhysicalW = (int)Math.Round(320.0 * dpiX / 96.0);
        int popupPhysicalH = (int)Math.Round(120.0 * dpiY / 96.0);
        var popupPreferredSize = new PhysicalSize(popupPhysicalW, popupPhysicalH);

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, popupPreferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        window.Left = dipRect.X;
        window.Top = dipRect.Y;
        window.Width = dipRect.Width;
        window.Height = Math.Max(dipRect.Height, 120);

        window.ShowError(shortMessage);
        window.SetLastLayoutDipRect(dipRect);

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

    public void Hide()
    {
        foreach (var window in _windows.Values)
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
            result[kvp.Key] = (hwnd, kvp.Value.LastLayoutDipRect);
        }
        return result;
    }
}
