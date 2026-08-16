using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.App.Coordination;
using QuickTranslate.TextToSpeech;

namespace QuickTranslate.App.Services;

public class WpfBlockPopupService : IBlockPopupService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly IMonitorService _monitorService;
    private readonly IOptions<AppSettings> _appSettings;
    private readonly ITextToSpeechService? _textToSpeech;
    private BlockPopupWindow? _window;
    private MonitorId _lastMonitorId = MonitorId.Empty;
    private uint _lastDpiX;
    private uint _lastDpiY;

    public WpfBlockPopupService(IDpiMapper dpiMapper, IMonitorService monitorService, IOptions<AppSettings> appSettings, ITextToSpeechService? textToSpeech = null)
    {
        _dpiMapper = dpiMapper;
        _monitorService = monitorService;
        _appSettings = appSettings;
        _textToSpeech = textToSpeech;
    }

    private static void RunOnUi(Action a)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher == null)
        {
            throw new InvalidOperationException(
                "No WPF dispatcher is available (both Application.Current and Dispatcher.CurrentDispatcher are null). " +
                "WPF UI services cannot operate on an MTA thread without a host Application instance.");
        }

        if (dispatcher.CheckAccess()) a();
        else dispatcher.Invoke(DispatcherPriority.Normal, a);
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
        RunOnUi(() => ShowCore(blockSelection, translation, monitorId, anchorBox, dpiX, dpiY));
    }

    private void ShowCore(BlockSelectionResult blockSelection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY)
    {
        EnsureWindow(monitorId, dpiX, dpiY);

        var monitors = _monitorService.EnumerateMonitors();
        var monitorInfo = monitors.FirstOrDefault(m => m.Id == monitorId)
                         ?? _monitorService.TryGetPrimary()
                         ?? new MonitorInfo(monitorId, string.Empty,
                             new PhysicalRect(0, 0, 1920, 1080),
                             new PhysicalRect(0, 0, 1920, 1080),
                             dpiX, dpiY, true);

        // 尺寸按原文/译文行数自适应（360~720 宽），替代旧固定 440x480
        var (estW, estH) = PopupSizeEstimator.EstimateBlockPopupSize(
            blockSelection.BlockText, translation.FullTranslation,
            monitorInfo.WorkArea.Width * 96.0 / dpiX,
            monitorInfo.WorkArea.Height * 96.0 / dpiY);

        var preferredSize = new PhysicalSize(
            (int)Math.Round(estW * dpiX / 96.0),
            (int)Math.Round(estH * dpiY / 96.0));

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, preferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        _window.Left = dipRect.X;
        _window.Top = dipRect.Y;
        _window.Width = dipRect.Width;
        _window.Height = Math.Max(dipRect.Height, Math.Min(140, estH));

        _window.ResetContent(blockSelection.BlockText);
        _window.AppendChunk(translation.FullTranslation ?? "");
        _window.UpdateHeader(blockSelection.SelectedLines?.Count ?? 0);
        _window.ApplyTextToSpeech(_textToSpeech, _appSettings.Value.EnableTextToSpeech, _appSettings.Value.TargetLanguage);

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
        RunOnUi(() => ShowErrorCore(monitorId, anchorBox, dpiX, dpiY, shortMessage, operationId));
    }

    private void ShowErrorCore(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
    {
        EnsureWindow(monitorId, dpiX, dpiY);

        var monitors = _monitorService.EnumerateMonitors();
        var monitorInfo = monitors.FirstOrDefault(m => m.Id == monitorId)
                         ?? _monitorService.TryGetPrimary()
                         ?? new MonitorInfo(monitorId, string.Empty,
                             new PhysicalRect(0, 0, 1920, 1080),
                             new PhysicalRect(0, 0, 1920, 1080),
                             dpiX, dpiY, true);

        var (errW, errH) = PopupSizeEstimator.EstimateBlockPopupSize(
            shortMessage, null,
            monitorInfo.WorkArea.Width * 96.0 / dpiX,
            monitorInfo.WorkArea.Height * 96.0 / dpiY);
        var preferredSize = new PhysicalSize(
            (int)Math.Round(errW * dpiX / 96.0),
            (int)Math.Round(errH * dpiY / 96.0));

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, preferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        _window.Left = dipRect.X;
        _window.Top = dipRect.Y;
        _window.Width = dipRect.Width;
        _window.Height = Math.Max(dipRect.Height, Math.Min(120, errH));

        _window.ResetContent(null);
        _window.ShowError(shortMessage);
        _window.ApplyTextToSpeech(null, false, null);

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
        RunOnUi(HideAllCore);
    }

    private void HideAllCore()
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
