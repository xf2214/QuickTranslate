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
            using var frame = await _captureService.CaptureAroundAsync(
                cursor, new PhysicalSize(720, 320), newSlot.Cts.Token).ConfigureAwait(false);
            captureRegion = frame.Region;

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Ocr);
            var ocr = await _ocrEngine.RecognizeAsync(frame, newSlot.Cts.Token).ConfigureAwait(false);

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Selecting);
            var sel = _wordSelector.SelectWord(ocr, cursor, null);
            selectionBox = sel.Box;

            // 诊断日志：记录选词框物理坐标 + DPI，定位"框尺寸不准"问题
            if (!sel.NoTextFound)
            {
                _logger.LogDebug(
                    "WordSelect: Text='{Text}' Box=({X},{Y},{W}x{H}) Cursor=({CX},{CY}) DPI=({DX},{DY}) Lines={LC}",
                    sel.Text, sel.Box.X, sel.Box.Y, sel.Box.Width, sel.Box.Height,
                    cursor.X, cursor.Y, dpiX, dpiY, ocr.Lines.Count);
            }

            if (IsStaleOrCanceled(newSlot)) return;

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

            _overlayService.Show(sel.Box, mid, dpiX, dpiY);
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

    public void CancelAll(bool returnIdle)
    {
        var old = Interlocked.Exchange(ref _current, null);
        if (old != null)
        {
            old.Cts.Cancel();
            old.Cts.Dispose();
        }
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
