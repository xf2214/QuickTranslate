using System.Windows.Interop;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.App.Coordination;

namespace QuickTranslate.App.Services;

public class WpfBlockPopupService : IBlockPopupService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly IMonitorService _monitorService;
    private BlockPopupWindow? _window;
    private MonitorId _lastMonitorId = MonitorId.Empty;
    private uint _lastDpiX;
    private uint _lastDpiY;

    public WpfBlockPopupService(IDpiMapper dpiMapper, IMonitorService monitorService)
    {
        _dpiMapper = dpiMapper;
        _monitorService = monitorService;
    }

    private void EnsureWindow(MonitorId monitorId, uint dpiX, uint dpiY)
    {
        bool monitorChanged = _lastMonitorId != monitorId;
        bool dpiChanged = !PerMonitorDpiHelpers.AreClose(_lastDpiX, dpiX) || !PerMonitorDpiHelpers.AreClose(_lastDpiY, dpiY);

        if (_window != null && (monitorChanged || dpiChanged))
        {
            _window.Close();
            _window = null;
        }

        if (_window == null)
        {
            _window = new BlockPopupWindow();
            _lastMonitorId = monitorId;
            _lastDpiX = dpiX;
            _lastDpiY = dpiY;
        }
        else
        {
            _lastMonitorId = monitorId;
            _lastDpiX = dpiX;
            _lastDpiY = dpiY;
        }
    }

    public void Show(BlockSelectionResult blockSelection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY)
    {
        EnsureWindow(monitorId, dpiX, dpiY);

        var monitors = _monitorService.EnumerateMonitors();
        var monitorInfo = monitors.FirstOrDefault(m => m.Id == monitorId)
                         ?? _monitorService.TryGetPrimary()
                         ?? new MonitorInfo(monitorId, string.Empty,
                             new PhysicalRect(0, 0, 1920, 1080),
                             new PhysicalRect(0, 0, 1920, 1080),
                             dpiX, dpiY, true);

        var preferredSize = _window!.GetPreferredPhysicalSize(dpiX, dpiY);

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, preferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        _window.Left = dipRect.X;
        _window.Top = dipRect.Y;
        _window.Width = dipRect.Width;
        _window.Height = Math.Max(dipRect.Height, 120);

        _window.AppendChunk(translation.FullTranslation ?? "");
        _window.UpdateHeader(blockSelection.SelectedLines?.Count ?? 0);

        if (!_window.IsVisible)
        {
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                hwnd = new WindowInteropHelper(_window).EnsureHandle();
            }
            _window.Show();
        }
        else
        {
            _window.Activate();
        }
    }

    public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
    {
        EnsureWindow(monitorId, dpiX, dpiY);

        var monitors = _monitorService.EnumerateMonitors();
        var monitorInfo = monitors.FirstOrDefault(m => m.Id == monitorId)
                         ?? _monitorService.TryGetPrimary()
                         ?? new MonitorInfo(monitorId, string.Empty,
                             new PhysicalRect(0, 0, 1920, 1080),
                             new PhysicalRect(0, 0, 1920, 1080),
                             dpiX, dpiY, true);

        var preferredSize = _window!.GetPreferredPhysicalSize(dpiX, dpiY);

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, preferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        _window.Left = dipRect.X;
        _window.Top = dipRect.Y;
        _window.Width = dipRect.Width;
        _window.Height = Math.Max(dipRect.Height, 120);

        _window.ShowError(shortMessage);

        if (!_window.IsVisible)
        {
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                hwnd = new WindowInteropHelper(_window).EnsureHandle();
            }
            _window.Show();
        }
        else
        {
            _window.Activate();
        }
    }

    public void HideAll()
    {
        if (_window != null)
        {
            _window.Hide();
            _window = null;
            _lastMonitorId = MonitorId.Empty;
            _lastDpiX = 0;
            _lastDpiY = 0;
        }
    }
}
