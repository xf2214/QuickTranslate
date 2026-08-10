using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;

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
    private readonly ITranslationProvider _translationProvider;
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
        ITranslationProvider translationProvider,
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
        _translationProvider = translationProvider;
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
                cursor, new PhysicalSize(720, 320), newSlot.Cts.Token);

            if (newSlot != _current || newSlot.Cts.IsCancellationRequested) return;

            SetState(newSlot, AppState.Ocr);
            var ocr = await _ocrEngine.RecognizeAsync(frame, newSlot.Cts.Token);

            if (newSlot != _current || newSlot.Cts.IsCancellationRequested) return;

            SetState(newSlot, AppState.Selecting);
            var sel = _wordSelector.SelectWord(ocr, cursor, null);

            if (newSlot != _current || newSlot.Cts.IsCancellationRequested) return;

            SetState(newSlot, AppState.OverlayVisible);
            if (sel.NoTextFound)
            {
                _overlayService.HideAll();
                _popupService.HideAll();
                return;
            }

            _overlayService.Show(sel.Box, mid, monitorInfo.DpiX, monitorInfo.DpiY);

            if (newSlot != _current || newSlot.Cts.IsCancellationRequested) return;

            SetState(newSlot, AppState.Translating);
            var trans = await _translationProvider.TranslateWordAsync(
                sel.Text ?? "", _settings.Value.TargetLanguage, newSlot.Cts.Token);

            if (newSlot != _current || newSlot.Cts.IsCancellationRequested) return;

            SetState(newSlot, AppState.Displaying);
            _popupService.Show(sel, trans, mid, sel.Box, dpiX, dpiY);
        }
        catch (OperationCanceledException)
        {
            _overlayService.HideAll();
            _popupService.HideAll();
            SetState(null, AppState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Word pipeline error");
            _overlayService.HideAll();
            _popupService.HideAll();
            SetState(null, AppState.Idle);
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
