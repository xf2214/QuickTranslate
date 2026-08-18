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

public class WpfWordPopupService : IWordPopupService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly IMonitorService _monitorService;
    private readonly IOptions<AppSettings> _appSettings;
    private readonly ITextToSpeechService? _textToSpeech;
    private readonly Dictionary<MonitorId, (WordPopupWindow window, uint dpiX, uint dpiY)> _windows = new();

    public WpfWordPopupService(IDpiMapper dpiMapper, IMonitorService monitorService, IOptions<AppSettings> appSettings, ITextToSpeechService? textToSpeech = null)
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

    public void Show(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX = 96, uint dpiY = 96)
    {
        RunOnUi(() => ShowCore(selection, translation, monitorId, anchorBox, dpiX, dpiY));
    }

    private void ShowCore(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY)
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

        WordPopupWindow window;
        if (needsRecreate || !_windows.TryGetValue(monitorId, out entry))
        {
            window = new WordPopupWindow();
            _windows[monitorId] = (window, dpiX, dpiY);
        }
        else
        {
            window = entry.window;
            _windows[monitorId] = (window, dpiX, dpiY);
        }

        var monitors = _monitorService.EnumerateMonitors();
        var monitorInfo = monitors.FirstOrDefault(m => m.Id == monitorId)
                         ?? _monitorService.TryGetPrimary()
                         ?? new MonitorInfo(monitorId, string.Empty,
                             new PhysicalRect(0, 0, 1920, 1080),
                             new PhysicalRect(0, 0, 1920, 1080),
                             dpiX, dpiY, true);

        // 尺寸随内容自适应（区分 CJK/ASCII 字宽），替代旧固定 320x150：
        // 短译文不再有大片空白，长译文自动换行增高并受工作区钳制
        var (estW, estH) = PopupSizeEstimator.EstimateWordPopupSize(
            selection.Text, translation.TargetText,
            monitorInfo.WorkArea.Width * 96.0 / dpiX,
            monitorInfo.WorkArea.Height * 96.0 / dpiY);

        int popupPhysicalW = (int)Math.Round(estW * dpiX / 96.0);
        int popupPhysicalH = (int)Math.Round(estH * dpiY / 96.0);
        var popupPreferredSize = new PhysicalSize(popupPhysicalW, popupPhysicalH);

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, popupPreferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        window.Left = dipRect.X;
        window.Top = dipRect.Y;
        window.Width = dipRect.Width;
        window.Height = Math.Max(dipRect.Height, estH);

        window.ApplyContent(selection, translation);
        window.ApplyTextToSpeech(_textToSpeech, _appSettings.Value.EnableTextToSpeech, _appSettings.Value.TargetLanguage);
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
        else
        {
            // 已可见时复用：取消进行中的退场动画并复播进场
            window.ReplayEntry();
        }
    }

    public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
    {
        RunOnUi(() => ShowErrorCore(monitorId, anchorBox, dpiX, dpiY, shortMessage, operationId));
    }

    private void ShowErrorCore(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
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

        WordPopupWindow window;
        if (needsRecreate || !_windows.TryGetValue(monitorId, out entry))
        {
            window = new WordPopupWindow();
            _windows[monitorId] = (window, dpiX, dpiY);
        }
        else
        {
            window = entry.window;
            _windows[monitorId] = (window, dpiX, dpiY);
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
        window.ApplyTextToSpeech(null, false, null);
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
        else
        {
            // 已可见时复用：取消进行中的退场动画并复播进场
            window.ReplayEntry();
        }
    }

    public void Hide()
    {
        RunOnUi(HideCore);
    }

    private void HideCore()
    {
        foreach (var kvp in _windows)
        {
            kvp.Value.window.HideWithFade();
        }
    }

    public void HideAll()
    {
        RunOnUi(HideAllCore);
    }

    private void HideAllCore()
    {
        foreach (var kvp in _windows)
        {
            kvp.Value.window.HideWithFade();
        }
    }

    public IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect()
    {
        var result = new Dictionary<MonitorId, (IntPtr, DipRect)>();
        foreach (var kvp in _windows)
        {
            IntPtr hwnd = new WindowInteropHelper(kvp.Value.window).Handle;
            result[kvp.Key] = (hwnd, kvp.Value.window.LastLayoutDipRect);
        }
        return result;
    }
}
