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
    private readonly ISelectionOverlayService? _overlayService;
    private readonly Dictionary<MonitorId, (WordPopupWindow window, uint dpiX, uint dpiY)> _windows = new();

    public WpfWordPopupService(IDpiMapper dpiMapper, IMonitorService monitorService, IOptions<AppSettings> appSettings, ITextToSpeechService? textToSpeech = null, ISelectionOverlayService? overlayService = null)
    {
        _dpiMapper = dpiMapper;
        _monitorService = monitorService;
        _appSettings = appSettings;
        _textToSpeech = textToSpeech;
        _overlayService = overlayService;
    }

    /// <summary>弹窗退场即藏选区：关闭按钮/5s 超时/服务收起全经窗口 HideWithFade 触发。</summary>
    private void OnWindowDismissed(object? sender, EventArgs e) => _overlayService?.HideAll();

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

    private static void RunOnUiAsync(Action a)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher == null)
        {
            throw new InvalidOperationException(
                "No WPF dispatcher is available (both Application.Current and Dispatcher.CurrentDispatcher are null). " +
                "WPF UI services cannot operate on an MTA thread without a host Application instance.");
        }

        if (dispatcher.CheckAccess()) a();
        else _ = dispatcher.InvokeAsync(a, DispatcherPriority.Normal);
    }

    public void Show(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX = 96, uint dpiY = 96)
    {
        // Keep blocking Invoke: Show requires HWND creation + measured placement (Place/Dpi mapping)
        // to complete before callers continue — strict ordering avoids racing Show vs subsequent
        // HideWithFade/ReplayEntry and ensures popup geometry is committed synchronously.
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
            window.Dismissed += OnWindowDismissed;
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
                              PhysicalRect.Fallback1080p,
                              PhysicalRect.Fallback1080p,
                              dpiX, dpiY, true);

        // 尺寸随内容自适应（区分 CJK/ASCII 字宽），替代旧固定 320x150：
        // 短译文不再有大片空白，长译文自动换行增高并受工作区钳制。
        // 用与弹窗展示一致的格式化文本估算（词典释义含多行），避免高度估小；
        // 词头估算需包含音标后缀（词典命中展示在词头行），否则长单词换行高度被低估
        var displayText = TranslationDisplayFormatter.ForWord(translation.TargetText, translation.FromDictionary);
        var headerForEstimate = BuildHeaderForEstimate(selection.Text, displayText, translation.FromDictionary);
        var (estW, estH) = PopupSizeEstimator.EstimateWordPopupSize(
            headerForEstimate, displayText,
            monitorInfo.WorkArea.Width * 96.0 / dpiX,
            monitorInfo.WorkArea.Height * 96.0 / dpiY);

        int popupPhysicalW = (int)Math.Round(estW * dpiX / 96.0);
        int popupPhysicalH = (int)Math.Round(estH * dpiY / 96.0);
        var popupPreferredSize = new PhysicalSize(popupPhysicalW, popupPhysicalH);

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, popupPreferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        // 物理像素定位：绕开 WPF DIP 换算依赖窗口创建时刻 DPI 认知的时序问题
        // （高缩放单屏/混合 DPI/运行中改分辨率时 DIP 定位按错误比例缩放）
        WindowPhysicalPlacement.SetPhysicalBounds(window, physicalRect, dpiX, dpiY, padDip: 0, topmost: false);
        // DIP 高度下限特调：物理定位后窗口 DPI 已与目标屏一致，DIP 换算自洽
        window.Height = Math.Max(dipRect.Height, estH);

        var style = _appSettings.Value.PopupDisplayStyle;
        bool detailed = !string.Equals(style, "compact", StringComparison.OrdinalIgnoreCase);
        window.ApplyDisplayMode(detailed);
        window.ApplyContent(selection, translation);
        window.ApplyTextToSpeech(_textToSpeech, _appSettings.Value.EnableTextToSpeech, _appSettings.Value.TargetLanguage);

        // 最终防线：ApplyContent 后按真实渲染测量校正高度。
        // 结构化词典视图/元信息行等动态布局不受估算常数漂移影响，只增不减（钳制工作区 45%），
        // 杜绝"底部按钮显示不全"。宽度不变，定位沿用 Place 结果。
        double desiredH = window.MeasureDesiredContentHeight();
        double maxHDip = Math.Max(140, monitorInfo.WorkArea.Height * 96.0 / dpiY * 0.45);
        window.Height = Math.Clamp(Math.Max(estH, Math.Ceiling(desiredH)), 110, maxHDip);

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

    /// <summary>词典命中的音标行展示在词头行，估算词头宽度/换行时必须把它算进文本。</summary>
    private static string BuildHeaderForEstimate(string? word, string displayText, bool fromDictionary)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return string.Empty;
        }

        if (!fromDictionary || displayText.Length == 0)
        {
            return word;
        }

        var nl = displayText.IndexOf('\n');
        var firstLine = (nl > 0 ? displayText[..nl] : displayText).Trim();
        return $"{word}  {firstLine}";
    }

    public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId)
    {
        // Keep blocking Invoke for same reason as Show — HWND/placement must be committed
        // synchronously before error state is visible; Hide/Replay ordering depends on it.
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
            window.Dismissed += OnWindowDismissed;
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
                              PhysicalRect.Fallback1080p,
                              PhysicalRect.Fallback1080p,
                              dpiX, dpiY, true);

        int popupPhysicalW = (int)Math.Round(320.0 * dpiX / 96.0);
        int popupPhysicalH = (int)Math.Round(120.0 * dpiY / 96.0);
        var popupPreferredSize = new PhysicalSize(popupPhysicalW, popupPhysicalH);

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, popupPreferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        // 物理像素定位：与 ShowCore 同一策略，绕开 DIP 换算的 DPI 认知时序问题
        WindowPhysicalPlacement.SetPhysicalBounds(window, physicalRect, dpiX, dpiY, padDip: 0, topmost: false);
        window.Height = Math.Max(dipRect.Height, 120);

        var style2 = _appSettings.Value.PopupDisplayStyle;
        bool detailed2 = !string.Equals(style2, "compact", StringComparison.OrdinalIgnoreCase);
        window.ApplyDisplayMode(detailed2);
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
        RunOnUiAsync(HideCore);
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
        RunOnUiAsync(HideAllCore);
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
