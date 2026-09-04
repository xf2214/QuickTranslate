using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.App.Coordination;

public class WordInteractionCoordinator : IInteractionCoordinator
{
    public class OperationSlot
    {
        public Guid Id { get; }
        public CancellationTokenSource Cts { get; }
        public AppState State { get; set; }

        public OperationSlot(Guid id, CancellationTokenSource cts, AppState state)
        {
            Id = id;
            Cts = cts;
            State = state;
        }
    }

    private readonly IAppLifecycle _appLifecycle;
    private readonly IHotkeyBroker _hotkeyBroker;
    private readonly ICursorService _cursorService;
    private readonly IMonitorService _monitorService;
    private readonly IScreenCapture _captureService;
    private readonly IOcrEngine _ocrEngine;
    private readonly IWordSelector _wordSelector;
    private readonly ISelectionOverlayService _overlayService;
    private readonly ITranslationRouter _translationRouter;
    private readonly IWordPopupService _popupService;
    private readonly IStatusIndicatorService? _statusIndicator;
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<WordInteractionCoordinator> _logger;
    private readonly ISelectedTextProbe? _selectedTextProbe;

    private volatile OperationSlot? _current;
    private readonly object _stateLock = new();

    // 翻译前让选定框至少可见的时长（毫秒）：本地词典/缓存瞬时翻译时，
    // 若不等一拍就弹结果，用户会完全看不到“指示翻译范围”的选定框。
    private const int SelectionHoldMs = 250;

    // 选定框自动消失时长见 SelectionOptions.SelectionAutoHideMs（Word/Block 共用，测试可反射调小）。

    // 截图范围按"15 个词宽 × 4 行高"估算：OCR 前未知真实行高，先用行高估值起捕，
    // OCR 后若选词触碰截图边缘（可能被截断），用识别到的实际行高重新算尺寸再抓一次。
    private const int TargetWordCount = 15;
    private const int TargetLineCount = 4;
    // 96-DPI 基准估算行高（物理像素）：高 DPI 屏（150%/200% 缩放）上文字物理尺寸同比放大，
    // 运行时经 DpiScale.Px 按 monitorInfo.DpiY 缩放后再使用，否则首捕范围/焦点带偏小
    private const int EstimatedLineHeight = 20;
    private const double AvgWordWidthPerLineHeight = 0.62;
    // 96-DPI 基准触边判定裕度（物理像素），运行时按 DpiX 缩放
    private const int EdgeClipMargin = 10;

    // 首捕宽度系数：估行高 20px 对大字号/网页标题严重偏小（日志实测行框 30-38px），
    // 首捕宽 186px 几乎必然横向截断——日志中每次首捕都触发 clipped 重试（二次 OCR
    // 延迟翻倍），且截边字形产生 'Tra'/'QkTransa' 类碎片词。首捕宽度加倍后绝大多数
    // 场景一轮完成；重抓逻辑仍以实际行高兜底，只增不减。
    private const double InitialCaptureWidthFactor = 2.0;

    private CancellationTokenSource? _selectionAutoHideCts;

    private static PhysicalSize WordCaptureSize(int lineHeight)
    {
        int width = (int)Math.Round(TargetWordCount * AvgWordWidthPerLineHeight * lineHeight);
        int height = TargetLineCount * lineHeight;
        return new PhysicalSize(Math.Max(1, width), Math.Max(1, height));
    }

    public AppState State { get; private set; } = AppState.Idle;

    public OperationSlot? CurrentSlot => _current;

    public WordInteractionCoordinator(
        IAppLifecycle appLifecycle,
        IHotkeyBroker hotkeyBroker,
        ICursorService cursorService,
        IMonitorService monitorService,
        IScreenCapture captureService,
        IOcrEngine ocrEngine,
        IWordSelector wordSelector,
        ISelectionOverlayService overlayService,
        ITranslationRouter translationRouter,
        IWordPopupService popupService,
        IOptions<AppSettings> settings,
        ILogger<WordInteractionCoordinator> logger,
        IStatusIndicatorService? statusIndicator = null,
        ISelectedTextProbe? selectedTextProbe = null)
    {
        _appLifecycle = appLifecycle;
        _hotkeyBroker = hotkeyBroker;
        _cursorService = cursorService;
        _monitorService = monitorService;
        _captureService = captureService;
        _ocrEngine = ocrEngine;
        _wordSelector = wordSelector;
        _overlayService = overlayService;
        _translationRouter = translationRouter;
        _popupService = popupService;
        _statusIndicator = statusIndicator;
        _settings = settings;
        _logger = logger;

        _selectedTextProbe = selectedTextProbe;
        _hotkeyBroker.HotkeyFired += OnHotkeyFired;
    }

    private void OnHotkeyFired(object? sender, HotkeyEvent e)
    {
        HandleHotkey(e);
    }

    public void HandleHotkey(HotkeyEvent hotkeyEvent)
    {
        if (_appLifecycle.IsPaused &&
            (hotkeyEvent.Type == HotkeyEventType.Word || hotkeyEvent.Type == HotkeyEventType.Block))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("App paused, ignoring hotkey {Type}", hotkeyEvent.Type);
            return;
        }

        switch (hotkeyEvent.Type)
        {
            case HotkeyEventType.Word:
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Word hotkey received at {Timestamp}, starting word pipeline", hotkeyEvent.Timestamp);
                // try 内已处理业务异常；此处只兜 try 之前的前置代码（定位/建槽/指示器），避免 UnobservedTaskException
                _ = RunWordPipeline().ContinueWith(t =>
                {
                    try { _logger.LogDebug(t.Exception, "[WordCoord] Pipeline faulted before handler (non-fatal)"); } catch { }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                break;
            case HotkeyEventType.Block:
                // Block 热键独立由 AppHost.WireBlockHotkey 订阅 broker.HotkeyFired 并调用
                // BlockInteractionCoordinator.RunBlockPipeline()。
                // 这里留空是为了避免 WordCoord / BlockCoord 重复触发 Block 流程。
                break;
            case HotkeyEventType.Escape:
                CancelAll(returnIdle: true);
                break;
        }
    }

    private async Task RunWordPipeline()
    {
        var cursor = _cursorService.GetPhysicalCursorPos(out var monitorId);
        var monitorInfo = _monitorService.TryGetMonitorFromPoint(cursor)
                          ?? _monitorService.TryGetPrimary()
                          ?? throw new InvalidOperationException("No monitor available");

        var dpiX = monitorInfo.DpiX;
        var dpiY = monitorInfo.DpiY;
        var mid = monitorInfo.Id;
        PhysicalRect? selectionBox = null;
        PhysicalRect? captureRegion = null;

        var newSlot = new OperationSlot(Guid.NewGuid(), new CancellationTokenSource(), AppState.Capturing);
        var oldSlot = Interlocked.Exchange(ref _current, newSlot);
        if (oldSlot != null)
        {
            oldSlot.Cts.Cancel();
            oldSlot.Cts.Dispose();
        }

        SetState(newSlot, AppState.Capturing);

        // 立即显示"识别中"反馈：OCR 首次推理可达数百毫秒，无反馈会被误以为热键失灵
        _statusIndicator?.Show(mid, new PhysicalRect(cursor.X - 4, cursor.Y - 4, 8, 8), dpiX, dpiY, "正在识别…");

        try
        {
            // 选中文本预探测放在截屏之前：若用户已显式选中文本（浏览器/编辑器等支持 UIA 的场景），
            // 可零 OCR 误差直接取词，跳过截屏/OCR/选词全链路，节省数百毫秒并避免识别偏差。
            SelectionResult? probeSel = await TryProbeWordAsync(newSlot, cursor).ConfigureAwait(false);
            if (probeSel != null)
            {
                if (IsStaleOrCanceled(newSlot)) return;
                // 选中文本命中：跳过截屏/OCR/选词，也跳过 250ms hold（用户显式选中无需展示识别范围）
                _overlayService.Show(probeSel.Box, mid, dpiX, dpiY);
                selectionBox = probeSel.Box;
                long overlayShownAt = Environment.TickCount64;
                if (IsStaleOrCanceled(newSlot)) return;
                await TranslateAndDisplayWordAsync(newSlot, probeSel, mid, dpiX, dpiY, overlayShownAt, skipHold: true).ConfigureAwait(false);
                return;
            }
            if (IsStaleOrCanceled(newSlot)) return;

            // ===== 截图 + OCR + 选词（最多两次：首次按估值行高，触边时用实际行高扩大重抓）=====
            // 高 DPI 屏：96-DPI 基准常量按监视器 DPI 缩放，保证首捕范围/焦点带/触边裕度的
            // 逻辑大小与 96-DPI 屏一致（96 DPI 下缩放系数为 1，行为不变）
            int estLineHeight = DpiScale.Px(EstimatedLineHeight, dpiY);
            int clipMargin = DpiScale.Px(EdgeClipMargin, dpiX);

            SelectionResult sel;
            var initialSize = WordCaptureSize(estLineHeight);
            using (var firstFrame = await _captureService.CaptureAroundAsync(
                cursor,
                new PhysicalSize(
                    (int)Math.Round(initialSize.Width * InitialCaptureWidthFactor),
                    initialSize.Height),
                newSlot.Cts.Token).ConfigureAwait(false))
            {
                captureRegion = firstFrame.Region;
                if (IsStaleOrCanceled(newSlot)) return;

                // 选取前先出范围框：虚线标出本次截图范围，让用户知道将识别哪片区域
                _overlayService.Show(captureRegion.Value, mid, dpiX, dpiY, preview: true);

                SetState(newSlot, AppState.Ocr);
                // 焦点带限定：取词只用光标所在行，带外行不跑 rec（扩抓后行多时收益显著）
                var ocr = await _ocrEngine.RecognizeAsync(
                    firstFrame, WordFocusBand(cursor, estLineHeight, firstFrame.Region), newSlot.Cts.Token).ConfigureAwait(false);
                if (IsStaleOrCanceled(newSlot)) return;

                SetState(newSlot, AppState.Selecting);
                sel = _wordSelector.SelectWord(ocr, cursor, null);
                LogWordSelection(sel, cursor, dpiX, dpiY, ocr.Lines.Count, captureRegion, "first");
                if (IsStaleOrCanceled(newSlot)) return;

                // 重抓触发条件：
                //   1) 选词框触碰截图边缘 —— 词被截断（日志实测：长标识符被截成 'tions.cs' 等后缀片段）；
                //   2) 未识别到任何文字 —— 范围可能太小错过文本；
                //   3) 选框明显偏离光标且光标附近的行触碰截图边缘 —— 光标下的词被截断/漏检，
                //      选择器退而选了邻近词（段落首尾常见：首词/末词贴截图边缘），扩大后重抓。
                // 扩大策略：触边方向尺寸翻倍（未识别到则双向翻倍），并以实际行高重算尺寸为下限，
                // 保证重抓范围只增不减（旧实现小字号时重算尺寸反而更小，截断永远修不好）。
                int anchorLineHeight = GetLineHeightAtAnchor(ocr, cursor) ?? estLineHeight;
                bool clipped = !sel.NoTextFound && TouchesCaptureEdge(sel.Box, captureRegion.Value, clipMargin);
                bool offCursor = !sel.NoTextFound && CursorOffSelection(sel.Box, cursor, anchorLineHeight);
                bool nearLineTouchesH = !sel.NoTextFound && LineNearCursorTouchesEdge(ocr, cursor, captureRegion.Value, horizontal: true, clipMargin);
                bool nearLineTouchesV = !sel.NoTextFound && LineNearCursorTouchesEdge(ocr, cursor, captureRegion.Value, horizontal: false, clipMargin);
                bool offCursorWithClippedAnchorLine = offCursor && (nearLineTouchesH || nearLineTouchesV);
                if (clipped || sel.NoTextFound || offCursorWithClippedAnchorLine)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug(
                            "WordRetry: reason clipped={Clipped} noText={NoText} offCursor={Off}(nearLineH={H},nearLineV={V})",
                            clipped, sel.NoTextFound, offCursorWithClippedAnchorLine, nearLineTouchesH, nearLineTouchesV);
                    var computed = WordCaptureSize(anchorLineHeight);
                    bool expandW = sel.NoTextFound || TouchesHorizontalEdge(sel.Box, captureRegion.Value, clipMargin) || (offCursor && nearLineTouchesH);
                    bool expandH = sel.NoTextFound || TouchesVerticalEdge(sel.Box, captureRegion.Value, clipMargin) || (offCursor && nearLineTouchesV);
                    var retrySize = new PhysicalSize(
                        Math.Max(expandW ? captureRegion.Value.Width * 2 : captureRegion.Value.Width, computed.Width),
                        Math.Max(expandH ? captureRegion.Value.Height * 2 : captureRegion.Value.Height, computed.Height));
                    using var retryFrame = await _captureService.CaptureAroundAsync(
                        cursor, retrySize, newSlot.Cts.Token).ConfigureAwait(false);
                    captureRegion = retryFrame.Region;
                    if (IsStaleOrCanceled(newSlot)) return;

                    _overlayService.Show(captureRegion.Value, mid, dpiX, dpiY, preview: true);

                    var retryOcr = await _ocrEngine.RecognizeAsync(
                        retryFrame, WordFocusBand(cursor, Math.Max(estLineHeight, anchorLineHeight), retryFrame.Region), newSlot.Cts.Token).ConfigureAwait(false);
                    if (IsStaleOrCanceled(newSlot)) return;

                    sel = _wordSelector.SelectWord(retryOcr, cursor, null);
                    LogWordSelection(sel, cursor, dpiX, dpiY, retryOcr.Lines.Count, captureRegion, "retry");
                    if (IsStaleOrCanceled(newSlot)) return;
                }
            }

            SetState(newSlot, AppState.OverlayVisible);
            if (sel.NoTextFound)
            {
                _overlayService.HideAll();
                _statusIndicator?.Hide();
                PhysicalRect hintBox = captureRegion ?? new PhysicalRect(cursor.X - 150, cursor.Y - 60, 300, 120);
                if (!IsStaleOrCanceled(newSlot))
                {
                    _popupService.ShowError(mid, hintBox, dpiX, dpiY,
                        "未检测到可翻译的文字，请将鼠标移到单词或文字上再按 Alt+1", newSlot.Id);
                }
                else
                {
                    _popupService.HideAll();
                }
                SetState(null, AppState.Idle);
                return;
            }

            // 显示前先钳制到截图区域内：选词框坐标来自 OCR 估算，
            // 不允许超出用户刚看到的虚线范围框，避免“框比截图范围还大”的观感。
            if (captureRegion.HasValue)
            {
                sel = sel with { Box = ClampToRegion(sel.Box, captureRegion.Value) };
            }

            _overlayService.Show(sel.Box, mid, dpiX, dpiY);
            selectionBox = sel.Box;
            long overlayShownAt2 = Environment.TickCount64;

            if (IsStaleOrCanceled(newSlot)) return;

            await TranslateAndDisplayWordAsync(newSlot, sel, mid, dpiX, dpiY, overlayShownAt2, skipHold: false).ConfigureAwait(false);
        }
        catch (TranslationException te)
        {
            _logger.LogWarning("Word pipeline translation error: Code={Code}, Message={Message}", te.ErrorCode, te.Message);
            _overlayService.HideAll();
            _statusIndicator?.Hide();
            var errorBox = selectionBox ?? captureRegion;
            if (errorBox.HasValue && !IsStaleOrCanceled(newSlot))
            {
                _popupService.ShowError(mid, errorBox.Value, dpiX, dpiY, TranslationErrorMessage.ForCode(te.ErrorCode), newSlot.Id);
            }
            else
            {
                _popupService.HideAll();
            }
            SetState(null, AppState.Idle);
        }
        catch (Core.Ocr.OcrException oex) when (oex.ErrorCode == Core.Ocr.OcrErrorCode.Cancelled)
        {
            _logger.LogDebug("Operation {Id} OCR cancelled", newSlot.Id);
            _overlayService.HideAll();
            _statusIndicator?.Hide();
            _popupService.HideAll();
            SetState(null, AppState.Idle);
        }
        catch (Core.Ocr.OcrException oex)
        {
            _logger.LogError(oex, "Word pipeline OCR error: Code={Code}", oex.ErrorCode);
            _overlayService.HideAll();
            _statusIndicator?.Hide();
            var errorBox = selectionBox ?? captureRegion;
            if (errorBox.HasValue && !IsStaleOrCanceled(newSlot))
            {
                var msg = oex.ErrorCode switch
                {
                    Core.Ocr.OcrErrorCode.ModelLoadFailed => "OCR 模型加载失败",
                    Core.Ocr.OcrErrorCode.InferenceFailed => "OCR 识别失败，请重试",
                    _ => "OCR 处理失败"
                };
                _popupService.ShowError(mid, errorBox.Value, dpiX, dpiY, msg, newSlot.Id);
            }
            else
            {
                _popupService.HideAll();
            }
            SetState(null, AppState.Idle);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Operation {Id} cancelled", newSlot.Id);
            _overlayService.HideAll();
            _statusIndicator?.Hide();
            _popupService.HideAll();
            SetState(null, AppState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Word pipeline error");
            _overlayService.HideAll();
            _statusIndicator?.Hide();
            var errorBox = selectionBox ?? captureRegion;
            if (errorBox.HasValue && !IsStaleOrCanceled(newSlot))
            {
                _popupService.ShowError(mid, errorBox.Value, dpiX, dpiY, "翻译失败，请稍后再试", newSlot.Id);
            }
            else
            {
                _popupService.HideAll();
            }
            SetState(null, AppState.Idle);
        }
    }

    private async Task<SelectionResult?> TryProbeWordAsync(OperationSlot slot, PhysicalPoint cursor)
    {
        if (!_settings.Value.EnableSelectedTextProbe || _selectedTextProbe is null) return null;
        try
        {
            var result = await _selectedTextProbe.ProbeAsync(cursor, slot.Cts.Token).ConfigureAwait(false);
            if (result is null) return null;
            var text = SelectedTextProbePolicy.Normalize(result.Text);
            if (!SelectedTextProbePolicy.IsAdoptable(text, SelectedTextProbePolicy.WordModeMaxChars)) return null;
            // 空间相关性校验：文档残留的旧选区远离光标时拒绝采纳，回退 OCR 指向用户正指的词
            if (!SelectedTextProbePolicy.IsSpatiallyRelevant(result.LineRects, result.UnionBox, cursor, wordMode: true))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("[Probe] Selection not near cursor, fall back to OCR");
                return null;
            }
            return new SelectionResult(text, text, result.UnionBox, SelectionKind.Word, null, slot.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SelectedText probe failed, fallback to OCR");
            return null;
        }
    }

    private async Task TranslateAndDisplayWordAsync(OperationSlot slot, SelectionResult sel, MonitorId mid, uint dpiX, uint dpiY, long overlayShownAt, bool skipHold)
    {
        if (IsStaleOrCanceled(slot)) return;
        SetState(slot, AppState.Translating);
        _statusIndicator?.Update("正在翻译…");
        var targetLang = ContainsCjk(sel.Text ?? string.Empty) ? "en" : _settings.Value.TargetLanguage;
        var trans = await _translationRouter.TranslateWordAsync(sel.Text ?? "", targetLang, slot.Cts.Token).ConfigureAwait(false);
        if (IsStaleOrCanceled(slot)) return;
        if (!skipHold && !trans.FromCache && !trans.FromDictionary)
        {
            long elapsed = Environment.TickCount64 - overlayShownAt;
            int remaining = SelectionHoldMs - (int)elapsed;
            // 分级 hold：缓存/词典命中已在上方短路为 0，在线翻译封顶 120ms（原 250ms，仅 debug 可选）
            remaining = Math.Min(remaining, 120);
            if (remaining > 0)
            {
                try
                {
                    await Task.Delay(remaining, slot.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (IsStaleOrCanceled(slot)) return;
            }
        }
        SetState(slot, AppState.Displaying);
        _statusIndicator?.Hide();
        _popupService.Show(sel, trans, mid, sel.Box, dpiX, dpiY);
        ScheduleSelectionAutoHide(slot);
    }

    private bool IsStaleOrCanceled(OperationSlot slot)
    {
        return slot != Volatile.Read(ref _current) || slot.Cts.IsCancellationRequested;
    }

    private void LogWordSelection(SelectionResult sel, PhysicalPoint cursor, uint dpiX, uint dpiY, int lineCount,
        PhysicalRect? captureRegion = null, string phase = "first")
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        // 诊断日志：记录选词框物理坐标 + DPI + 截图范围，定位"检测/选取位置不准"问题
        string capture = captureRegion.HasValue
            ? $"{captureRegion.Value.X},{captureRegion.Value.Y} {captureRegion.Value.Width}x{captureRegion.Value.Height}"
            : "n/a";
        if (!sel.NoTextFound)
        {
            _logger.LogDebug(
                "WordSelect[{Phase}]: Text='{Text}' Box=({X},{Y},{W}x{H}) Cursor=({CX},{CY}) DPI=({DX},{DY}) Lines={LC} Capture={CR}",
                phase, sel.Text, sel.Box.X, sel.Box.Y, sel.Box.Width, sel.Box.Height,
                cursor.X, cursor.Y, dpiX, dpiY, lineCount, capture);
        }
        else
        {
            _logger.LogDebug(
                "WordSelect[{Phase}]: NoTextFound Cursor=({CX},{CY}) Lines={LC} Capture={CR}",
                phase, cursor.X, cursor.Y, lineCount, capture);
        }
    }

    private static bool TouchesCaptureEdge(PhysicalRect box, PhysicalRect region, int margin)
    {
        return TouchesHorizontalEdge(box, region, margin) || TouchesVerticalEdge(box, region, margin);
    }

    private static bool TouchesHorizontalEdge(PhysicalRect box, PhysicalRect region, int margin)
    {
        return box.Left - region.Left < margin ||
               region.Right - box.Right < margin;
    }

    private static bool TouchesVerticalEdge(PhysicalRect box, PhysicalRect region, int margin)
    {
        return box.Top - region.Top < margin ||
               region.Bottom - box.Bottom < margin;
    }

    /// <summary>
    /// 选框是否明显偏离光标：水平容差按行高缩放（行首/行尾指向常略出词框），
    /// 垂直容差给足一行（词框垂直收紧只贴墨水，光标在同行内垂直偏离属正常）。
    /// </summary>
    private static bool CursorOffSelection(PhysicalRect box, PhysicalPoint cursor, int lineHeight)
    {
        int tolX = Math.Max(4, lineHeight / 3);
        int tolY = Math.Max(6, lineHeight);
        bool offX = cursor.X < box.Left - tolX || cursor.X > box.Right + tolX;
        bool offY = cursor.Y < box.Top - tolY || cursor.Y > box.Bottom + tolY;
        return offX || offY;
    }

    /// <summary>
    /// 离光标最近的 OCR 行是否触碰截图边缘：是则说明光标所在行被截断，
    /// 光标下的词可能漏检/框偏，值得扩大截图重抓（段落首尾失败的典型特征）。
    /// </summary>
    private static bool LineNearCursorTouchesEdge(Core.Ocr.OcrLayoutResult ocr, PhysicalPoint cursor, PhysicalRect region, bool horizontal, int margin)
    {
        Core.Ocr.OcrLine? nearest = null;
        double best = double.MaxValue;
        foreach (var line in ocr.Lines)
        {
            double dist = RectDistance(cursor, line.Box);
            if (dist < best)
            {
                best = dist;
                nearest = line;
            }
        }
        if (nearest == null) return false;

        return horizontal
            ? TouchesHorizontalEdge(nearest.Box, region, margin)
            : TouchesVerticalEdge(nearest.Box, region, margin);
    }

    private static double RectDistance(PhysicalPoint p, PhysicalRect box)
    {
        int dx = 0;
        if (p.X < box.Left) dx = box.Left - p.X;
        else if (p.X >= box.Right) dx = p.X - (box.Right - 1);

        int dy = 0;
        if (p.Y < box.Top) dy = box.Top - p.Y;
        else if (p.Y >= box.Bottom) dy = p.Y - (box.Bottom - 1);

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static PhysicalRect ClampToRegion(PhysicalRect box, PhysicalRect region)
    {
        int x1 = Math.Max(box.Left, region.Left);
        int y1 = Math.Max(box.Top, region.Top);
        int x2 = Math.Min(box.Right, region.Right);
        int y2 = Math.Min(box.Bottom, region.Bottom);
        if (x2 <= x1 || y2 <= y1) return box;
        return new PhysicalRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>
    /// Word 模式焦点带：光标 ±1.5 行高（引擎只识别与带相交的行）。
    /// 取词语义上只用光标所在行，容差覆盖行高估值误差与光标在行内的垂直位置；
    /// 不带时扩抓后捕获区内所有行都会跑 rec（实测 ~75ms/行），远行永远不会被选中。
    /// </summary>
    private static PhysicalRect WordFocusBand(PhysicalPoint cursor, int lineHeight, PhysicalRect frameRegion)
    {
        int half = Math.Max(1, lineHeight * 3 / 2);
        return new PhysicalRect(frameRegion.X, cursor.Y - half, frameRegion.Width, half * 2);
    }

    private static int? GetLineHeightAtAnchor(Core.Ocr.OcrLayoutResult ocr, PhysicalPoint anchor)
    {
        var containing = ocr.Lines.FirstOrDefault(l => l.Box.Contains(anchor));
        if (containing != null) return containing.Box.Height;
        var nearest = ocr.Lines
            .OrderBy(l => Math.Abs(l.Box.Y + l.Box.Height / 2 - anchor.Y))
            .FirstOrDefault();
        return nearest?.Box.Height;
    }

    /// <summary>选定框展示后 SelectionAutoHideMs 自动收起（Popup 保留）；新任务/Esc 会取消。</summary>
    private void ScheduleSelectionAutoHide(OperationSlot slot)
    {
        _selectionAutoHideCts?.Cancel();
        _selectionAutoHideCts?.Dispose();
        var cts = new CancellationTokenSource();
        _selectionAutoHideCts = cts;
        // AutoHide 内部只 catch 取消；HideAll 等意外异常由这里观察，避免 UnobservedTaskException
        _ = AutoHideOverlayAsync(slot, cts.Token).ContinueWith(t =>
        {
            try { _logger.LogDebug(t.Exception, "[WordCoord] Auto-hide faulted (non-fatal)"); } catch { }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task AutoHideOverlayAsync(OperationSlot slot, CancellationToken token)
    {
        try
        {
            await Task.Delay(SelectionOptions.SelectionAutoHideMs, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // 仅当该操作仍是当前任务时才收起，避免误隐藏新任务的选定框
        if (!IsStaleOrCanceled(slot))
        {
            _overlayService.HideAll();
        }
    }

    public void CancelAll(bool returnIdle)
    {
        var old = Interlocked.Exchange(ref _current, null);
        if (old != null)
        {
            old.Cts.Cancel();
            old.Cts.Dispose();
        }
        _selectionAutoHideCts?.Cancel();
        _selectionAutoHideCts?.Dispose();
        _selectionAutoHideCts = null;
        _overlayService.HideAll();
        _statusIndicator?.Hide();
        _popupService.HideAll();
        if (returnIdle)
        {
            SetState(null, AppState.Idle);
        }
    }

    private void SetState(OperationSlot? slot, AppState s)
    {
        lock (_stateLock)
        {
            State = s;
            if (slot != null)
            {
                slot.State = s;
            }
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("State -> {S}", s);
        }
    }

    public void ShowTranslationPopup(string sourceText, string translatedText)
    {
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF ||
                c >= 0x3040 && c <= 0x30FF || c >= 0xAC00 && c <= 0xD7AF)
                return true;
        }
        return false;
    }

    public void ShowSettingsWindow()
    {
    }
}
