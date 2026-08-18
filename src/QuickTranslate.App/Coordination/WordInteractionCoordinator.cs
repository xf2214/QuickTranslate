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

    private volatile OperationSlot? _current;
    private readonly object _stateLock = new();

    // 翻译前让选定框至少可见的时长（毫秒）：本地词典/缓存瞬时翻译时，
    // 若不等一拍就弹结果，用户会完全看不到“指示翻译范围”的选定框。
    private const int SelectionHoldMs = 250;

    // 选定框自动消失时长（毫秒）：Popup 出现后选定框无需常驻，超时自动收起，Esc 可提前关闭。
    // 非 const：测试可通过反射调小以避免真实等待。
    private static int SelectionAutoHideMs = 3000;

    // 截图范围按“15 个词宽 × 4 行高”估算：OCR 前未知真实行高，先用行高估值起捕，
    // OCR 后若选词触碰截图边缘（可能被截断），用识别到的实际行高重新算尺寸再抓一次。
    private const int TargetWordCount = 15;
    private const int TargetLineCount = 4;
    private const int EstimatedLineHeight = 20;
    private const double AvgWordWidthPerLineHeight = 0.62;
    private const int EdgeClipMargin = 10;

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
        IStatusIndicatorService? statusIndicator = null)
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
            _logger.LogDebug("App paused, ignoring hotkey {Type}", hotkeyEvent.Type);
            return;
        }

        switch (hotkeyEvent.Type)
        {
            case HotkeyEventType.Word:
                _logger.LogDebug("Word hotkey received at {Timestamp}, starting word pipeline", hotkeyEvent.Timestamp);
                _ = RunWordPipeline();
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
            // ===== 截图 + OCR + 选词（最多两次：首次按估值行高，触边时用实际行高扩大重抓）=====
            SelectionResult sel;
            using (var firstFrame = await _captureService.CaptureAroundAsync(
                cursor, WordCaptureSize(EstimatedLineHeight), newSlot.Cts.Token).ConfigureAwait(false))
            {
                captureRegion = firstFrame.Region;
                if (IsStaleOrCanceled(newSlot)) return;

                // 选取前先出范围框：虚线标出本次截图范围，让用户知道将识别哪片区域
                _overlayService.Show(captureRegion.Value, mid, dpiX, dpiY, preview: true);

                SetState(newSlot, AppState.Ocr);
                var ocr = await _ocrEngine.RecognizeAsync(firstFrame, newSlot.Cts.Token).ConfigureAwait(false);
                if (IsStaleOrCanceled(newSlot)) return;

                SetState(newSlot, AppState.Selecting);
                sel = _wordSelector.SelectWord(ocr, cursor, null);
                LogWordSelection(sel, cursor, dpiX, dpiY, ocr.Lines.Count);
                if (IsStaleOrCanceled(newSlot)) return;

                // 重抓触发条件：
                //   1) 选词框触碰截图边缘 —— 词被截断（日志实测：长标识符被截成 'tions.cs' 等后缀片段）；
                //   2) 未识别到任何文字 —— 范围可能太小错过文本。
                // 扩大策略：触边方向尺寸翻倍（未识别到则双向翻倍），并以实际行高重算尺寸为下限，
                // 保证重抓范围只增不减（旧实现小字号时重算尺寸反而更小，截断永远修不好）。
                bool clipped = !sel.NoTextFound && TouchesCaptureEdge(sel.Box, captureRegion.Value);
                if (clipped || sel.NoTextFound)
                {
                    int lineHeight = GetLineHeightAtAnchor(ocr, cursor) ?? EstimatedLineHeight;
                    var computed = WordCaptureSize(lineHeight);
                    bool expandW = sel.NoTextFound || TouchesHorizontalEdge(sel.Box, captureRegion.Value);
                    bool expandH = sel.NoTextFound || TouchesVerticalEdge(sel.Box, captureRegion.Value);
                    var retrySize = new PhysicalSize(
                        Math.Max(expandW ? captureRegion.Value.Width * 2 : captureRegion.Value.Width, computed.Width),
                        Math.Max(expandH ? captureRegion.Value.Height * 2 : captureRegion.Value.Height, computed.Height));
                    using var retryFrame = await _captureService.CaptureAroundAsync(
                        cursor, retrySize, newSlot.Cts.Token).ConfigureAwait(false);
                    captureRegion = retryFrame.Region;
                    if (IsStaleOrCanceled(newSlot)) return;

                    _overlayService.Show(captureRegion.Value, mid, dpiX, dpiY, preview: true);

                    var retryOcr = await _ocrEngine.RecognizeAsync(retryFrame, newSlot.Cts.Token).ConfigureAwait(false);
                    if (IsStaleOrCanceled(newSlot)) return;

                    sel = _wordSelector.SelectWord(retryOcr, cursor, null);
                    LogWordSelection(sel, cursor, dpiX, dpiY, retryOcr.Lines.Count);
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
            long overlayShownAt = Environment.TickCount64;

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Translating);
            _statusIndicator?.Update("正在翻译…");
            // 中文词 → 译成英文；非中文（英文等）→ 用户设置的目标语言（默认中文）。
            // 避免 TargetLanguage=zh-CN 时中文词"中译中"原样回显。
            var targetLang = ContainsCjk(sel.Text ?? string.Empty)
                ? "en"
                : _settings.Value.TargetLanguage;
            var trans = await _translationRouter.TranslateWordAsync(
                sel.Text ?? "", targetLang, newSlot.Cts.Token).ConfigureAwait(false);

            if (IsStaleOrCanceled(newSlot)) return;

            // 保证选定框至少可见 SelectionHoldMs，让用户先看清翻译范围再弹结果
            long elapsed = Environment.TickCount64 - overlayShownAt;
            int remaining = SelectionHoldMs - (int)elapsed;
            if (remaining > 0)
            {
                try
                {
                    await Task.Delay(remaining, newSlot.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (IsStaleOrCanceled(newSlot)) return;
            }

            SetState(newSlot, AppState.Displaying);
            _statusIndicator?.Hide();
            _popupService.Show(sel, trans, mid, sel.Box, dpiX, dpiY);
            // 自动隐藏从 Popup 出现时开始计时，保证选定框至少与 Popup 同时可见 SelectionAutoHideMs
            ScheduleSelectionAutoHide(newSlot);
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

    private bool IsStaleOrCanceled(OperationSlot slot)
    {
        return slot != Volatile.Read(ref _current) || slot.Cts.IsCancellationRequested;
    }

    private void LogWordSelection(SelectionResult sel, PhysicalPoint cursor, uint dpiX, uint dpiY, int lineCount)
    {
        // 诊断日志：记录选词框物理坐标 + DPI，定位"框尺寸不准"问题
        if (!sel.NoTextFound)
        {
            _logger.LogDebug(
                "WordSelect: Text='{Text}' Box=({X},{Y},{W}x{H}) Cursor=({CX},{CY}) DPI=({DX},{DY}) Lines={LC}",
                sel.Text, sel.Box.X, sel.Box.Y, sel.Box.Width, sel.Box.Height,
                cursor.X, cursor.Y, dpiX, dpiY, lineCount);
        }
    }

    private static bool TouchesCaptureEdge(PhysicalRect box, PhysicalRect region)
    {
        return TouchesHorizontalEdge(box, region) || TouchesVerticalEdge(box, region);
    }

    private static bool TouchesHorizontalEdge(PhysicalRect box, PhysicalRect region)
    {
        return box.Left - region.Left < EdgeClipMargin ||
               region.Right - box.Right < EdgeClipMargin;
    }

    private static bool TouchesVerticalEdge(PhysicalRect box, PhysicalRect region)
    {
        return box.Top - region.Top < EdgeClipMargin ||
               region.Bottom - box.Bottom < EdgeClipMargin;
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
        _ = AutoHideOverlayAsync(slot, cts.Token);
    }

    private async Task AutoHideOverlayAsync(OperationSlot slot, CancellationToken token)
    {
        try
        {
            await Task.Delay(SelectionAutoHideMs, token).ConfigureAwait(false);
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
