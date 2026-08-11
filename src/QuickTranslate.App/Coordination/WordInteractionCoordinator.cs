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
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<WordInteractionCoordinator> _logger;

    private volatile OperationSlot? _current;
    private readonly object _stateLock = new();

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
        ILogger<WordInteractionCoordinator> logger)
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
                _ = RunWordPipeline();
                break;
            case HotkeyEventType.Block:
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

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.OverlayVisible);
            if (sel.NoTextFound)
            {
                _overlayService.HideAll();
                _popupService.HideAll();
                return;
            }

            _overlayService.Show(sel.Box, mid, dpiX, dpiY);

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Translating);
            var trans = await _translationRouter.TranslateWordAsync(
                sel.Text ?? "", _settings.Value.TargetLanguage, newSlot.Cts.Token).ConfigureAwait(false);

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Displaying);
            _popupService.Show(sel, trans, mid, sel.Box, dpiX, dpiY);
        }
        catch (TranslationException te)
        {
            _logger.LogWarning("Word pipeline translation error: Code={Code}, Message={Message}", te.ErrorCode, te.Message);
            _overlayService.HideAll();
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
            _popupService.HideAll();
            SetState(null, AppState.Idle);
        }
        catch (Core.Ocr.OcrException oex)
        {
            _logger.LogError(oex, "Word pipeline OCR error: Code={Code}", oex.ErrorCode);
            _overlayService.HideAll();
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
            _popupService.HideAll();
            SetState(null, AppState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Word pipeline error");
            _overlayService.HideAll();
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

    public void ShowSettingsWindow()
    {
    }
}
