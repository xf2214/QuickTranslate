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
    // 节流间隔：流式 chunk 高频时周期性增长，避免每 chunk 都重排造成抖动与布局风暴
    private const int GrowthResizeThrottleMs = 100;

    private readonly IDpiMapper _dpiMapper;
    private readonly IMonitorService _monitorService;
    private readonly IOptions<AppSettings> _appSettings;
    private readonly ITextToSpeechService? _textToSpeech;
    private BlockPopupWindow? _window;
    private MonitorId _lastMonitorId = MonitorId.Empty;
    private uint _lastDpiX;
    private uint _lastDpiY;

    // 粘性重定位上下文：记录上次放置的锚点/工作区/矩形，供流式增长时同侧延伸
    private PhysicalRect _lastAnchorBox;
    private PhysicalRect _lastWorkArea;
    private PhysicalRect _lastPlacedRect;
    private bool _hasPlacementContext;

    // 节流+尾沿：持续流式时周期性增长，而非纯防抖的"停才动"
    private DispatcherTimer? _growthResizeTimer;
    private bool _resizePending;
    private long _lastResizeAppliedTicks; // Environment.TickCount64

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

    /// <summary>WorkArea 物理像素转 DIP 尺寸，供 Estimator 估算使用。</summary>
    private static (double wDip, double hDip) GetWorkAreaDipSize(PhysicalRect workArea, uint dpiX, uint dpiY)
    {
        return (workArea.Width * 96.0 / dpiX, workArea.Height * 96.0 / dpiY);
    }

    private void EnsureWindow(MonitorId monitorId, uint dpiX, uint dpiY)
    {
        bool monitorChanged = _lastMonitorId != monitorId;
        bool dpiChanged = !PerMonitorDpiHelpers.AreClose(_lastDpiX, dpiX) || !PerMonitorDpiHelpers.AreClose(_lastDpiY, dpiY);

        if (_window != null && (monitorChanged || dpiChanged))
        {
            // 窗口重建前退订，避免旧窗口泄漏事件持有
            _window.ContentGrew -= OnWindowContentGrew;
            StopGrowthTimer();
            _hasPlacementContext = false;
            _window.Close();
            _window = null;
        }

        if (_window == null)
        {
            _window = new BlockPopupWindow();
            _window.ContentGrew += OnWindowContentGrew;
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

        var (workWdip, workHdip) = GetWorkAreaDipSize(monitorInfo.WorkArea, dpiX, dpiY);
        // WHY：按当前显示模式估算尺寸（简洁折叠源文，详细含预览），与弹窗展示保持一致
        bool detailed = _appSettings.Value?.PopupDisplayStyle != "compact";
        // 尺寸按原文/译文行数自适应（340~640 宽），替代旧固定 440x480；
        // 译文用与展示一致的格式化文本估算（去多余空行），避免尺寸与实际内容不符
        var (estW, estH) = PopupSizeEstimator.EstimateBlockPopupSize(
            blockSelection.BlockText, TranslationDisplayFormatter.ForBlock(translation.FullTranslation),
            workWdip, workHdip, detailed);

        var preferredSize = new PhysicalSize(
            (int)Math.Round(estW * dpiX / 96.0),
            (int)Math.Round(estH * dpiY / 96.0));

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, preferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        // 记录粘性上下文，供后续流式 AppendChunk 触发的重算使用
        _lastAnchorBox = anchorBox;
        _lastWorkArea = monitorInfo.WorkArea;
        _lastPlacedRect = physicalRect;
        _hasPlacementContext = true;

        // 物理像素定位：绕开 WPF DIP 换算依赖窗口创建时刻 DPI 认知的时序问题
        // （高缩放单屏/混合 DPI/运行中改分辨率时 DIP 定位按错误比例缩放）
        WindowPhysicalPlacement.SetPhysicalBounds(_window, physicalRect, dpiX, dpiY, padDip: 0, topmost: false);
        // DIP 高度下限特调：物理定位后窗口 DPI 已与目标屏一致，DIP 换算自洽
        _window!.Height = Math.Max(dipRect.Height, Math.Min(140, estH));

        _window.ApplyDisplayMode(detailed);
        _window.ResetContent(blockSelection.BlockText);
        _window.AppendChunk(translation.FullTranslation ?? "");
        _window.UpdateHeader(blockSelection.SelectedLines?.Count ?? 0);
        _window.ApplyTextToSpeech(_textToSpeech, _appSettings.Value.EnableTextToSpeech, _appSettings.Value.TargetLanguage);
        // 非流式路径：一次呈现即完成，移除光标/翻转 meta 为完成态
        _window.MarkStreamCompleted();

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
            _window.ReplayEntry();
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

        var (workWdip, workHdip) = GetWorkAreaDipSize(monitorInfo.WorkArea, dpiX, dpiY);
        bool detailed = _appSettings.Value?.PopupDisplayStyle != "compact";
        // 错误消息渲染在译文区（ResetContent(null) + ShowError 写 TranslationText），故 source 为 null
        var (errW, errH) = PopupSizeEstimator.EstimateBlockPopupSize(
            null, shortMessage,
            workWdip, workHdip, detailed);
        var preferredSize = new PhysicalSize(
            (int)Math.Round(errW * dpiX / 96.0),
            (int)Math.Round(errH * dpiY / 96.0));

        var physicalRect = PopupPlacement.Place(anchorBox, monitorInfo.WorkArea, preferredSize);
        var dipRect = _dpiMapper.ToDip(physicalRect, dpiX, dpiY);

        _lastAnchorBox = anchorBox;
        _lastWorkArea = monitorInfo.WorkArea;
        _lastPlacedRect = physicalRect;
        _hasPlacementContext = true;

        // 物理像素定位：与 ShowCore 同一策略，绕开 DIP 换算的 DPI 认知时序问题
        WindowPhysicalPlacement.SetPhysicalBounds(_window, physicalRect, dpiX, dpiY, padDip: 0, topmost: false);
        _window!.Height = Math.Max(dipRect.Height, Math.Min(120, errH));

        _window.ApplyDisplayMode(detailed);
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
            _window.ReplayEntry();
            _window.Activate();
        }
    }

    public void MarkStreamCompleted()
    {
        RunOnUi(() => _window?.MarkStreamCompleted());
    }

    public void HideAll()
    {
        RunOnUi(HideAllCore);
    }

    private void HideAllCore()
    {
        if (_window != null)
        {
            _window.ContentGrew -= OnWindowContentGrew;
            StopGrowthTimer();
            _hasPlacementContext = false;
            // 淡出退场：动画完成后由 PopupTransition 内部执行 Hide()；
            // 此处先释放引用，下次 Show 将新建窗口，旧窗口随动画结束被 GC
            _window.HideWithFade();
            _window = null;
            _lastMonitorId = MonitorId.Empty;
            _lastDpiX = 0;
            _lastDpiY = 0;
        }
    }

    // ===== 流式粘性重算 =====

    /// <summary>
    /// 纯几何重算：按当前源文+译文估算 DIP 尺寸，转物理像素后用 PlaceSticky 同侧延伸。
    /// 不触碰任何 UI 属性，可单元测试。
    /// </summary>
    internal static PhysicalRect ComputeStickyResize(
        PhysicalRect anchorBox, PhysicalRect monitorWorkArea,
        string? sourceText, string? fullTranslationDisplayText,
        double workAreaWidthDip, double workAreaHeightDip,
        uint dpiX, uint dpiY, PhysicalRect lastPlacedRect, bool detailed = true, bool sourceExpanded = false)
    {
        var (estW, estH) = PopupSizeEstimator.EstimateBlockPopupSize(
            sourceText, fullTranslationDisplayText,
            workAreaWidthDip, workAreaHeightDip, detailed, sourceExpanded);

        var preferred = new PhysicalSize(
            (int)Math.Round(estW * dpiX / 96.0),
            (int)Math.Round(estH * dpiY / 96.0));

        return PopupPlacement.PlaceSticky(anchorBox, monitorWorkArea, preferred, lastPlacedRect);
    }

    private void OnWindowContentGrew(object? sender, EventArgs e)
    {
        // 护栏：窗口未就绪或上下文缺失时忽略（ShowCore 首次填充未 Show 前也会触发，由 IsVisible 天然过滤）
        if (_window == null || !_window.IsVisible || !_hasPlacementContext)
        {
            return;
        }

        long now = Environment.TickCount64;
        long elapsed = now - _lastResizeAppliedTicks;

        // 节流+尾沿：高频流式时周期性增长，而非纯防抖只在末尾动一次
        if (elapsed >= GrowthResizeThrottleMs)
        {
            ApplyGrowthResize();
            _lastResizeAppliedTicks = Environment.TickCount64;
        }
        else if (!_resizePending)
        {
            _resizePending = true;
            long remaining = GrowthResizeThrottleMs - elapsed;
            if (remaining <= 0) remaining = GrowthResizeThrottleMs;
            _growthResizeTimer ??= new DispatcherTimer();
            _growthResizeTimer.Interval = TimeSpan.FromMilliseconds(remaining);
            _growthResizeTimer.Tick += OnGrowthResizeTimerTick;
            _growthResizeTimer.Start();
        }
    }

    private void OnGrowthResizeTimerTick(object? sender, EventArgs e)
    {
        StopGrowthTimer();
        _resizePending = false;
        _lastResizeAppliedTicks = Environment.TickCount64;
        ApplyGrowthResize();
    }

    private void StopGrowthTimer()
    {
        if (_growthResizeTimer != null)
        {
            _growthResizeTimer.Stop();
            _growthResizeTimer.Tick -= OnGrowthResizeTimerTick;
            // 保留实例复用，避免高频创建
        }
        _resizePending = false;
    }

    private void ApplyGrowthResize()
    {
        if (_window == null || !_hasPlacementContext) return;

        var (workWdip, workHdip) = GetWorkAreaDipSize(_lastWorkArea, _lastDpiX, _lastDpiY);
        // 取当前窗口累积的完整译文（展示用格式化文本），与 ShowCore 保持一致的估算口径
        string displayText = TranslationDisplayFormatter.ForBlock(_window.FullText);
        string? srcText = _window.CurrentSourceText;

        bool detailed = _appSettings.Value?.PopupDisplayStyle != "compact";
        var newRect = ComputeStickyResize(
            _lastAnchorBox, _lastWorkArea,
            srcText, displayText,
            workWdip, workHdip,
            _lastDpiX, _lastDpiY, _lastPlacedRect, detailed, _window.IsSourceExpanded);

        // 单调护栏：流式只增不减，宽高都 ≤ 上次（1px 容差）则跳过，防止内容抖动时来回收缩
        if (newRect.Width <= _lastPlacedRect.Width + 1 && newRect.Height <= _lastPlacedRect.Height + 1)
        {
            return;
        }

        var dipRect = _dpiMapper.ToDip(newRect, _lastDpiX, _lastDpiY);

        // 与当前窗口 DIP 几何比较，无变化则跳过，避免重复布局
        bool sameLeft = Math.Abs(_window.Left - dipRect.X) <= 0.5;
        bool sameTop = Math.Abs(_window.Top - dipRect.Y) <= 0.5;
        bool sameW = Math.Abs(_window.Width - dipRect.Width) <= 0.5;
        bool sameH = Math.Abs(_window.Height - dipRect.Height) <= 0.5;
        if (sameLeft && sameTop && sameW && sameH)
        {
            return;
        }

        // 物理像素定位（流式增长重算）：绕开 DIP 换算的 DPI 认知时序问题
        WindowPhysicalPlacement.SetPhysicalBounds(_window, newRect, _lastDpiX, _lastDpiY, padDip: 0, topmost: false);
        _lastPlacedRect = newRect;
    }
}
