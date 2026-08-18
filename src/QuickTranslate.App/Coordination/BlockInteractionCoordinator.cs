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
    private readonly IStatusIndicatorService? _statusIndicator;
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<BlockInteractionCoordinator> _logger;
    private readonly IEscHook _escHook;

    private volatile OperationSlot? _current;
    private readonly object _stateLock = new();
    private bool _disposed;
    private bool _started;

    // 翻译前让区域选定框至少可见的时长（毫秒）：瞬时翻译时避免框一闪而过
    private const int SelectionHoldMs = 250;

    // 区域选定框自动消失时长（毫秒）：与 Word 模式保持一致，超时自动收起，Esc 可提前关闭。
    // 非 const：测试可通过反射调小以避免真实等待。
    private static int SelectionAutoHideMs = 3000;

    private CancellationTokenSource? _selectionAutoHideCts;

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
        IEscHook escHook,
        IStatusIndicatorService? statusIndicator = null)
    {
        _cursorService = cursorService;
        _monitorService = monitorService;
        _retryCoordinator = retryCoordinator;
        _overlayService = overlayService;
        _popupService = popupService;
        _translationRouter = translationRouter;
        _statusIndicator = statusIndicator;
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
        _statusIndicator?.Hide();
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

    public void TryCancelAndClear(bool returnIdle)
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

    public void RunBlockPipeline()
    {
        _logger.LogDebug("Block hotkey received, starting block pipeline");
        _ = RunBlockPipelineAsync();
    }

    private static string TruncateForLog(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "\"\"";
        var oneLine = text.Replace('\n', '⏎');
        return oneLine.Length <= maxLen ? $"'{oneLine}'" : $"'{oneLine[..maxLen]}…'";
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

        // 立即显示"识别中"反馈：块模式要做截图 + OCR 检索，无反馈期间最易被误判为卡死
        _statusIndicator?.Show(mid, new PhysicalRect(anchor.X - 4, anchor.Y - 4, 8, 8), dpiX, dpiY, "正在识别…");

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
                _statusIndicator?.Hide();
                if (!IsStaleOrCanceled(newSlot))
                {
                    _popupService.ShowError(mid, fallbackBox, dpiX, dpiY,
                        "未检测到段落文本，请将鼠标移到英文段落文字上再按 Alt+2", newSlot.Id);
                }
                else
                {
                    _popupService.HideAll();
                }
                SetState(null, AppState.Idle);
                return;
            }

            unionBox = block.UnionBox;
            // 诊断日志：记录选块几何 + 文本规模 + 逐行明细，定位“选块不准”问题
            _logger.LogDebug(
                "BlockSelect: Lines={Lines} Box=({X},{Y},{W}x{H}) TextLen={Len} Captures={Captures} Preview={Preview}",
                block.SelectedLines.Count, unionBox.Value.X, unionBox.Value.Y,
                unionBox.Value.Width, unionBox.Value.Height,
                block.BlockText?.Length ?? 0, captures,
                TruncateForLog(block.BlockText, 80));
            foreach (var line in block.SelectedLines)
            {
                _logger.LogDebug("  BlockLine: ({X},{Y},{W}x{H}) Text={Text}",
                    line.Box.X, line.Box.Y, line.Box.Width, line.Box.Height, TruncateForLog(line.Text, 40));
            }
            _overlayService.Show(block.UnionBox, mid, dpiX, dpiY);
            long overlayShownAt = Environment.TickCount64;

            if (IsStaleOrCanceled(newSlot)) return;

            SetState(newSlot, AppState.Translating);
            _statusIndicator?.Update("正在翻译…");
            var translation = await _translationRouter.TranslateBlockAsync(
                block.BlockText!, _settings.Value.TargetLanguage, newSlot.Cts.Token).ConfigureAwait(false);

            if (IsStaleOrCanceled(newSlot)) return;

            // 保证区域选定框至少可见 SelectionHoldMs，让用户先看清翻译范围再弹结果
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
            _popupService.Show(block, translation, mid, block.UnionBox, dpiX, dpiY);
            // 自动隐藏从 Popup 出现时开始计时，与 Word 模式策略一致
            ScheduleSelectionAutoHide(newSlot);
        }
        catch (TranslationException te)
        {
            _logger.LogWarning("Block pipeline translation error: Code={Code}, Message={Message}", te.ErrorCode, te.Message);
            _overlayService.HideAll();
            _statusIndicator?.Hide();
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
            _statusIndicator?.Hide();
            _popupService.HideAll();
            SetState(null, AppState.Idle);
        }
        catch (Core.Ocr.OcrException oex)
        {
            _logger.LogError(oex, "Block pipeline OCR error: Code={Code}", oex.ErrorCode);
            _overlayService.HideAll();
            _statusIndicator?.Hide();
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
            _statusIndicator?.Hide();
            _popupService.HideAll();
            SetState(null, AppState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Block pipeline error");
            _overlayService.HideAll();
            _statusIndicator?.Hide();
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
