using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Coordination;

namespace QuickTranslate.App.Coordination;

public class BlockInteractionCoordinator : IDisposable
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

    private readonly ICursorService _cursorService;
    private readonly IMonitorService _monitorService;
    private readonly BlockRetryCoordinator _retryCoordinator;
    private readonly ISelectionOverlayService _overlayService;
    private readonly IBlockPopupService _popupService;
    private readonly ITranslationRouter _translationRouter;
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<BlockInteractionCoordinator> _logger;
    private readonly IEscHook _escHook;

    private volatile OperationSlot? _current;
    private readonly object _stateLock = new();
    private bool _disposed;
    private bool _started;

    public AppState State { get; private set; } = AppState.Idle;

    public OperationSlot? CurrentSlot => _current;

    public BlockInteractionCoordinator(
        ICursorService cursorService,
        IMonitorService monitorService,
        BlockRetryCoordinator retryCoordinator,
        ISelectionOverlayService overlayService,
        IBlockPopupService popupService,
        ITranslationRouter translationRouter,
        IOptions<AppSettings> settings,
        ILogger<BlockInteractionCoordinator> logger,
        IEscHook escHook)
    {
        _cursorService = cursorService;
        _monitorService = monitorService;
        _retryCoordinator = retryCoordinator;
        _overlayService = overlayService;
        _popupService = popupService;
        _translationRouter = translationRouter;
        _settings = settings;
        _logger = logger;
        _escHook = escHook;

        _escHook.EscPressed += OnEscPressed;
    }

    private void OnEscPressed(object? sender, EventArgs e)
    {
        TryCancelAndClear(returnIdle: true);
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _logger.LogDebug("BlockInteractionCoordinator started");
    }

    public void Stop()
    {
        if (!_started || _disposed) return;
        TryCancelAndClear(returnIdle: true);
        _started = false;
        _logger.LogDebug("BlockInteractionCoordinator stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _escHook.EscPressed -= OnEscPressed;

        var old = Interlocked.Exchange(ref _current, null);
        if (old != null)
        {
            old.Cts.Cancel();
            old.Cts.Dispose();
        }

        _overlayService.HideAll();
        _popupService.HideAll();
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
            _logger.LogDebug("Block State -> {S}", s);
        }
    }

    private bool IsStaleOrCanceled(OperationSlot slot)
    {
        return slot != _current || slot.Cts.IsCancellationRequested;
    }

    public void TryCancelAndClear(bool returnIdle)
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

    public void RunBlockPipeline()
    {
        _ = RunBlockPipelineAsync();
    }

    private async Task RunBlockPipelineAsync()
    {
        if (_disposed) return;

        var anchor = _cursorService.GetPhysicalCursorPos(out var monitorId);
        var monitorInfo = _monitorService.TryGetMonitorFromPoint(anchor)
                          ?? _monitorService.TryGetPrimary()
                          ?? throw new InvalidOperationException("No monitor available");

        var dpiX = monitorInfo.DpiX;
        var dpiY = monitorInfo.DpiY;
        var mid = monitorInfo.Id;
        PhysicalRect? unionBox = null;
        PhysicalRect fallbackBox = new PhysicalRect(anchor.X - 100, anchor.Y - 50, 200, 100);

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
            var (ocr, block, captures) = await _retryCoordinator.SelectBlockWithRetryAsync(
                anchor, mid, dpiX, dpiY, newSlot.Cts.Token).ConfigureAwait(false);

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Selecting);
            SetState(newSlot, AppState.OverlayVisible);

            if (block.NoBlockFound)
            {
                _overlayService.HideAll();
                _popupService.HideAll();
                return;
            }

            unionBox = block.UnionBox;
            _overlayService.Show(block.UnionBox, mid, dpiX, dpiY);

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Translating);
            var translation = await _translationRouter.TranslateBlockAsync(
                block.BlockText!, _settings.Value.TargetLanguage, newSlot.Cts.Token).ConfigureAwait(false);

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Displaying);
            _popupService.Show(block, translation, mid, block.UnionBox, dpiX, dpiY);
        }
        catch (TranslationException te)
        {
            _logger.LogWarning("Block pipeline translation error: Code={Code}, Message={Message}", te.ErrorCode, te.Message);
            _overlayService.HideAll();
            var errorBox = unionBox ?? fallbackBox;
            if (!IsStaleOrCanceled(newSlot))
            {
                _popupService.ShowError(mid, errorBox, dpiX, dpiY, TranslationErrorMessage.ForCode(te.ErrorCode), newSlot.Id);
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
            _logger.LogError(oex, "Block pipeline OCR error: Code={Code}", oex.ErrorCode);
            _overlayService.HideAll();
            var errorBox = unionBox ?? fallbackBox;
            if (!IsStaleOrCanceled(newSlot))
            {
                var msg = oex.ErrorCode switch
                {
                    Core.Ocr.OcrErrorCode.ModelLoadFailed => "OCR 模型加载失败",
                    Core.Ocr.OcrErrorCode.InferenceFailed => "OCR 识别失败，请重试",
                    _ => "OCR 处理失败"
                };
                _popupService.ShowError(mid, errorBox, dpiX, dpiY, msg, newSlot.Id);
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
            _logger.LogError(ex, "Block pipeline error");
            _overlayService.HideAll();
            var errorBox = unionBox ?? fallbackBox;
            if (!IsStaleOrCanceled(newSlot))
            {
                _popupService.ShowError(mid, errorBox, dpiX, dpiY, "翻译失败，请稍后再试", newSlot.Id);
            }
            else
            {
                _popupService.HideAll();
            }
            SetState(null, AppState.Idle);
        }
    }
}
