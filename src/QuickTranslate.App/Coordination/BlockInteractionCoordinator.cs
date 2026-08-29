using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Ocr;
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
    private readonly ISelectedTextProbe? _selectedTextProbe;
    private readonly IHotkeyBroker? _hotkeyBroker;

    private volatile OperationSlot? _current;
    private readonly object _stateLock = new();
    private bool _disposed;
    private bool _started;

    // Dragging state machine (Task2+Task3/4: hold 400ms enters dragging, 16ms poll cursor Y to expand UnionBox)
    private DispatcherTimer? _dragTimer;
    private PhysicalPoint _anchorPoint;
    private OcrLayoutResult? _dragOcr;
    private PhysicalRect _dragInitialUnion;
    private PhysicalRect _lastExpandedUnion;
    private MonitorId _dragMonitorId;
    private uint _dragDpiX = 96;
    private uint _dragDpiY = 96;
    private bool _isDragging;
    private BlockSelectionResult? _dragBlock;
    private long _dragOverlayShownAt;
    private readonly object _dragLock = new();
    private PhysicalRect _dragCaptureRegion;
    private bool _hasExpandedCapture;
    private ScreenFrame? _dragFrame;
    private int _lastDragY = int.MinValue;
    private bool _pendingUpdate;

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
        IStatusIndicatorService? statusIndicator = null,
        ISelectedTextProbe? selectedTextProbe = null,
        IHotkeyBroker? hotkeyBroker = null)
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

        _selectedTextProbe = selectedTextProbe;
        _hotkeyBroker = hotkeyBroker;
        _escHook.EscPressed += OnEscPressed;
        if (_hotkeyBroker != null)
        {
            _hotkeyBroker.BlockHoldStateChanged += OnBlockHoldStateChanged;
        }
    }

    private void OnEscPressed(object? sender, EventArgs e)
    {
        StopDragging();
        TryCancelAndClear(returnIdle: true);
    }

    // === Dragging state machine (Task2) ===

    public static PhysicalRect ExpandSelectedLines(IReadOnlyList<OcrLine> lines, int anchorY, int dragY)
    {
        // 兼容旧测试：仍以 Y 为锚点，重载到带 X 的版本（X=0 近似）
        return ExpandSelectedLines(lines, new PhysicalPoint(0, anchorY), dragY);
    }

    public static PhysicalRect ExpandSelectedLines(IReadOnlyList<OcrLine> lines, PhysicalPoint anchor, int dragY)
    {
        if (lines.Count == 0) return PhysicalRect.Empty;
        var anchorLine = FindAnchorLine(lines, anchor);
        if (anchorLine == null) return PhysicalRect.Empty;
        if (dragY <= anchor.Y)
        {
            return anchorLine.Box;
        }
        // 行扩展策略：按 Y 排序后从锚点行向下收集，所有候选需通过段落启发式的
        // 轻量护栏（垂直间隙、高度比、水平相交），避免把下一段落或另一栏文本误吸入；
        // 同时受 dragY + snap 截断，保证“拖到哪选到哪”的显式语义。
        var sorted = lines.OrderBy(l => l.Box.Top).ToList();
        int anchorIdx = sorted.FindIndex(l => ReferenceEquals(l, anchorLine) || l.Box.Equals(anchorLine.Box));
        if (anchorIdx < 0) anchorIdx = 0;
        int medianH = ComputeMedianHeightForExpand(sorted, anchorLine);
        int snap = anchorLine.Box.Height / 2;
        double dragLimit = dragY + snap;
        var selected = new List<OcrLine> { anchorLine };
        PhysicalRect union = anchorLine.Box;
        PhysicalRect coreUnion = anchorLine.Box;
        for (int i = anchorIdx + 1; i < sorted.Count; i++)
        {
            var cand = sorted[i];
            if (cand.Box.Top > dragLimit) break;
            // 仅向下：跳过锚点上方行
            if (cand.Box.Top < anchorLine.Box.Top) continue;
            // 显式拖选：忽略段落垂直间隙护栏（用户明确拖到该 Y 就应选中），仅保留高度比与水平相交护栏，避免跨栏误吸
            if (!IsCandidateForDragExpand(cand, coreUnion, medianH)) continue;
            union = UnionRect(union, cand.Box);
            if (IsCoreWidth(cand, sorted, anchorLine))
                coreUnion = UnionRect(coreUnion, cand.Box);
            selected.Add(cand);
        }
        return UnionRect(selected);
    }

    private static int ComputeMedianHeightForExpand(List<OcrLine> sorted, OcrLine anchorLine)
    {
        if (sorted.Count < 3) return anchorLine.Box.Height;
        var heights = sorted.Select(l => l.Box.Height).OrderBy(h => h).ToList();
        return heights[heights.Count / 2];
    }

    private static bool TryComputeMedianGapForExpand(List<OcrLine> sorted, out int medianGap)
    {
        medianGap = 0;
        if (sorted.Count < 3) return false;
        var gaps = new List<int>(sorted.Count - 1);
        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1].Box;
            var cur = sorted[i].Box;
            if (cur.Right <= prev.Left || cur.Left >= prev.Right) continue;
            gaps.Add(Math.Max(0, cur.Top - prev.Bottom));
        }
        if (gaps.Count < 2) return false;
        gaps.Sort();
        medianGap = gaps[(gaps.Count - 1) / 2];
        return true;
    }

    private static bool IsCoreWidth(OcrLine cand, List<OcrLine> all, OcrLine anchorLine)
    {
        int widthBaseline = Math.Max(anchorLine.Box.Width, all.Select(l => l.Box.Width).OrderBy(w => w).ToList()[all.Count / 2]);
        int coreLimit = (int)Math.Round(widthBaseline * 1.2);
        return cand.Box.Width <= coreLimit;
    }

    private static bool IsCandidateForExpand(OcrLine cand, PhysicalRect union, PhysicalRect coreUnion, int medianH, double gapLimit)
    {
        double ratio = cand.Box.Height / (double)medianH;
        if (ratio < 0.65 || ratio > 1.5) return false;
        if (cand.Box.Right <= coreUnion.Left || cand.Box.Left >= coreUnion.Right) return false;
        int vGap = cand.Box.Top - union.Bottom;
        if (vGap > gapLimit) return false;
        double overlap = Math.Max(0, Math.Min(cand.Box.Right, coreUnion.Right) - Math.Max(cand.Box.Left, coreUnion.Left)) / (double)Math.Min(cand.Box.Width, coreUnion.Width);
        int leftDelta = Math.Abs(cand.Box.Left - coreUnion.Left);
        if (!(overlap >= 0.5 || leftDelta <= 1.5 * medianH)) return false;
        return true;
    }

    private static bool IsCandidateForDragExpand(OcrLine cand, PhysicalRect coreUnion, int medianH)
    {
        // 显式拖选：用户明确拖到该 Y 的行就应选中，不应因段落/栏的水平护栏被过滤；
        // 仅保留高度比护栏（过滤异常高/矮的 UI 栏），水平位置完全放开。
        double ratio = cand.Box.Height / (double)medianH;
        if (ratio < 0.65 || ratio > 1.5) return false;
        return true;
    }

    private static OcrLine? FindAnchorLine(IReadOnlyList<OcrLine> lines, int anchorY)
    {
        return FindAnchorLine(lines, new PhysicalPoint(0, anchorY));
    }

    private static OcrLine? FindAnchorLine(IReadOnlyList<OcrLine> lines, PhysicalPoint anchor)
    {
        foreach (var line in lines)
        {
            if (anchor.Y >= line.Box.Top && anchor.Y < line.Box.Bottom
                && anchor.X >= line.Box.Left && anchor.X < line.Box.Right)
                return line;
        }
        foreach (var line in lines)
        {
            if (anchor.Y >= line.Box.Top && anchor.Y < line.Box.Bottom)
                return line;
        }
        // nearest by distance to rect
        double best = double.MaxValue;
        OcrLine? bestLine = null;
        foreach (var line in lines)
        {
            double d = DistanceToRect(anchor, line.Box);
            if (d < best)
            {
                best = d;
                bestLine = line;
            }
        }
        return bestLine;
    }

    private static PhysicalRect UnionRect(IEnumerable<OcrLine> lines)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxR = int.MinValue, maxB = int.MinValue;
        bool any = false;
        foreach (var l in lines)
        {
            any = true;
            if (l.Box.X < minX) minX = l.Box.X;
            if (l.Box.Y < minY) minY = l.Box.Y;
            if (l.Box.Right > maxR) maxR = l.Box.Right;
            if (l.Box.Bottom > maxB) maxB = l.Box.Bottom;
        }
        if (!any) return PhysicalRect.Empty;
        return new PhysicalRect(minX, minY, maxR - minX, maxB - minY);
    }

    private static PhysicalRect UnionRect(PhysicalRect a, PhysicalRect b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new PhysicalRect(x, y, right - x, bottom - y);
    }

    private static double DistanceToRect(PhysicalPoint p, PhysicalRect box)
    {
        int dx = 0;
        if (p.X < box.Left) dx = box.Left - p.X;
        else if (p.X >= box.Right) dx = p.X - (box.Right - 1);
        int dy = 0;
        if (p.Y < box.Top) dy = box.Top - p.Y;
        else if (p.Y >= box.Bottom) dy = p.Y - (box.Bottom - 1);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void OnBlockHoldStateChanged(object? sender, HotkeyHoldEventArgs e)
    {
        if (e.Phase == HotkeyHoldPhase.Start)
        {
            HandleHoldStart();
        }
        else if (e.Phase == HotkeyHoldPhase.End)
        {
            HandleHoldEnd();
        }
    }

    private void HandleHoldStart()
    {
        if (_disposed) return;
        var anchor = _cursorService.GetPhysicalCursorPos(out var monitorId);
        var monitorInfo = _monitorService.TryGetMonitorFromPoint(anchor) ?? _monitorService.TryGetPrimary();
        if (monitorInfo != null)
        {
            _dragMonitorId = monitorInfo.Id;
            _dragDpiX = monitorInfo.DpiX;
            _dragDpiY = monitorInfo.DpiY;
        }
        else
        {
            _dragMonitorId = monitorId;
            _dragDpiX = 96;
            _dragDpiY = 96;
        }

        lock (_dragLock)
        {
            _anchorPoint = anchor;
            _hasExpandedCapture = false;
            // 复用判定：仅当锚点仍在上次 OCR 截图区域内才复用，避免“指针在 A 处、框显示在 B 处”的偏移
            bool canReuseOcr = _dragOcr != null && _dragOcr.Lines.Count > 0
                               && !_dragCaptureRegion.IsEmpty && _dragCaptureRegion.Contains(anchor);
            if (canReuseOcr)
            {
                // Initialize dragging with existing OCR
                _lastExpandedUnion = ExpandSelectedLines(_dragOcr!.Lines, _anchorPoint, _anchorPoint.Y);
                if (_lastExpandedUnion.IsEmpty)
                    _lastExpandedUnion = _dragInitialUnion;
                _logger.LogDebug("HoldStart: reuse _dragOcr lines={LC} capture={CR} initialUnion={Box}", _dragOcr!.Lines.Count, _dragCaptureRegion, _lastExpandedUnion);
            }
            else
            {
                if (_dragOcr != null)
                    _logger.LogDebug("HoldStart: discard stale _dragOcr lines={LC} capture={CR} anchor={Anchor}", _dragOcr.Lines.Count, _dragCaptureRegion, anchor);
                if (_dragInitialUnion.IsEmpty == false && _dragCaptureRegion.Contains(anchor))
                {
                    _lastExpandedUnion = _dragInitialUnion;
                    _logger.LogDebug("HoldStart: reuse _dragInitialUnion {Box}", _lastExpandedUnion);
                }
                else
                {
                    // Fallback: small box around anchor until new OCR arrives
                    _lastExpandedUnion = new PhysicalRect(anchor.X - 20, anchor.Y - 10, 40, 20);
                    _logger.LogDebug("HoldStart: fallback small box {Box} (no OCR yet or anchor out of capture)", _lastExpandedUnion);
                }
                // 失效旧 OCR，避免拖动期间 ExpandSelectedLines 命中远处旧行
                _dragOcr = null;
                _dragBlock = null;
            }
            _isDragging = true;
        }

        // Show initial preview (dragging state, no scan)
        var initialUnion = _lastExpandedUnion;
        _overlayService.Show(initialUnion, _dragMonitorId, _dragDpiX, _dragDpiY, preview: true);
        _dragOverlayShownAt = Environment.TickCount64;

        // If pipeline hasn't produced OCR yet, trigger it async in background
        if (_dragOcr == null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunBlockPipelineForDragAsync(anchor, _dragMonitorId, _dragDpiX, _dragDpiY).ConfigureAwait(false);
                }
                catch { }
            });
        }

        StartDragTimer();
        _logger.LogDebug("Block hold started at {Anchor}, dragging preview", anchor);
    }

    private async Task RunBlockPipelineForDragAsync(PhysicalPoint anchor, MonitorId mid, uint dpiX, uint dpiY)
    {
        if (_disposed || !_isDragging) return;
        var slot = _current;
        if (slot == null)
        {
            slot = new OperationSlot(Guid.NewGuid(), new CancellationTokenSource(), AppState.Capturing);
            var old = Interlocked.Exchange(ref _current, slot);
            old?.Cts.Cancel();
            old?.Cts.Dispose();
            SetState(slot, AppState.Capturing);
        }
        try
        {
            var (ocr, block, _) = await _retryCoordinator.SelectBlockWithRetryAsync(anchor, mid, dpiX, dpiY, slot.Cts.Token).ConfigureAwait(false);
            if (IsStaleOrCanceled(slot) || !_isDragging) return;
            lock (_dragLock)
            {
                _dragOcr = ocr;
                _dragBlock = block;
                _dragInitialUnion = block.UnionBox;
                _lastExpandedUnion = block.UnionBox;
                _dragCaptureRegion = ocr.CaptureRegion;
            }
            _logger.LogDebug("Drag pipeline OCR done: lines={LC} blockLines={BL} union={Box} capture={CR}",
                ocr.Lines.Count, block.SelectedLines.Count, block.UnionBox, ocr.CaptureRegion);
            foreach (var line in ocr.Lines)
                _logger.LogDebug("  DragOcrLine: ({X},{Y},{W}x{H}) Text={Text}", line.Box.X, line.Box.Y, line.Box.Width, line.Box.Height, TruncateForLog(line.Text, 40));
            _overlayService.Show(block.UnionBox, mid, dpiX, dpiY, preview: true);
            _dragOverlayShownAt = Environment.TickCount64;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Drag pipeline failed");
        }
    }

    private void HandleHoldEnd()
    {
        if (!_isDragging) return;
        StopDragTimer();
        PhysicalRect finalUnion;
        BlockSelectionResult? blockToTranslate;
        lock (_dragLock)
        {
            finalUnion = _lastExpandedUnion;
            _isDragging = false;
            // Build expanded block from dragOcr lines
            if (_dragOcr != null && _dragOcr.Lines.Count > 0)
            {
                var expandedBox = finalUnion;
                var expandedLines = new List<OcrLine>();
                // Re-derive which lines contributed to union (geometric filter)
                // Use same logic as ExpandSelectedLines but collect lines
                var anchorLine = FindAnchorLine(_dragOcr.Lines, _anchorPoint.Y);
                if (anchorLine != null)
                {
                    int anchorTop = anchorLine.Box.Top;
                    int dragY = expandedBox.Bottom;
                    int snap = anchorLine.Box.Height / 2;
                    foreach (var line in _dragOcr.Lines)
                    {
                        if (line.Box.Top < anchorTop) continue;
                        if (line.Box.Top <= dragY + snap) // includes last line
                            expandedLines.Add(line);
                        else if (expandedLines.Count > 0 && line.Box.Top <= expandedBox.Bottom + 5)
                            expandedLines.Add(line);
                    }
                    // Ensure union matches finalUnion: if expandedLines union != finalUnion, trust finalUnion
                    if (expandedLines.Count == 0 && _dragBlock != null)
                        expandedLines.AddRange(_dragBlock.SelectedLines);
                    var text = string.Join("\n", expandedLines.Select(l => l.Text));
                    var opId = _current?.Id ?? Guid.NewGuid();
                    blockToTranslate = new BlockSelectionResult(text, expandedBox, expandedLines.AsReadOnly(), SelectionKind.Block, opId, NoBlockFound: false);
                }
                else
                {
                    blockToTranslate = _dragBlock;
                }
            }
            else
            {
                blockToTranslate = _dragBlock;
            }
        }

        if (blockToTranslate == null)
        {
            // Fallback to current slot's block if available, or just hide
            _overlayService.HideAll();
            _statusIndicator?.Hide();
            SetState(null, AppState.Idle);
            return;
        }

        // Switch to solid scan + translation
        _overlayService.Show(finalUnion, _dragMonitorId, _dragDpiX, _dragDpiY, preview: false);
        var currentSlot = _current;
        if (currentSlot == null)
        {
            currentSlot = new OperationSlot(Guid.NewGuid(), new CancellationTokenSource(), AppState.OverlayVisible);
            var old = Interlocked.Exchange(ref _current, currentSlot);
            old?.Cts.Cancel();
            old?.Cts.Dispose();
        }
        SetState(currentSlot, AppState.OverlayVisible);
        _ = TranslateAndDisplayBlockAsync(currentSlot, blockToTranslate, _dragMonitorId, _dragDpiX, _dragDpiY, _dragOverlayShownAt, skipHold: false);
        _logger.LogDebug("Block hold ended, final union {Box}, translating", finalUnion);
    }

    private void StartDragTimer()
    {
        StopDragTimer();
        try
        {
            // HoldStart runs on hotkey hook thread (threadpool, no Dispatcher loop) — must
            // create timer on the UI dispatcher, otherwise ticks never fire (systematic root cause).
            // 32ms (30fps) + snap/2 节流已足够跟手，16ms 在 WPF 布局竞争下反而堆积 BeginInvoke 队列导致卡顿
            var dispatcher = System.Windows.Application.Current?.Dispatcher
                             ?? Dispatcher.CurrentDispatcher;
            var timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher);
            timer.Interval = TimeSpan.FromMilliseconds(32);
            timer.Tick += OnDragTick;
            _dragTimer = timer;
            _lastDragY = int.MinValue;
            _pendingUpdate = false;
            timer.Start();
            _logger.LogDebug("Drag timer started 32ms on dispatcher {Hash}", dispatcher.GetHashCode());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Drag timer failed to start");
            // Fallback for non-WPF thread (tests): no timer, manual ticks only
            _dragTimer = null;
        }
    }

    private void StopDragTimer()
    {
        var t = _dragTimer;
        if (t != null)
        {
            try { t.Stop(); } catch { }
            try { t.Tick -= OnDragTick; } catch { }
            _dragTimer = null;
        }
    }

    private void StopDragging()
    {
        lock (_dragLock)
        {
            _isDragging = false;
        }
        StopDragTimer();
        if (_dragFrame != null)
        {
            ((IDisposable)_dragFrame).Dispose();
            _dragFrame = null;
        }
    }

    private void OnDragTick(object? sender, EventArgs e)
    {
        _ = TriggerDragTickAsync();
    }

    internal async Task TriggerDragTickAsync()
    {
        if (!_isDragging)
            return;
        if (_dragOcr == null)
            return;
        var cur = _cursorService.GetPhysicalCursorPos(out var mid);
        var mi = _monitorService.TryGetMonitorFromPoint(cur);
        if (mi != null)
        {
            _dragMonitorId = mi.Id;
            _dragDpiX = mi.DpiX;
            _dragDpiY = mi.DpiY;
        }
        else
        {
            _dragMonitorId = mid;
        }

        // 节流：仅当 Y 变化 ≥ max(5, snap/2) 才重算 Expand，避免静止时无谓计算
        int snapForThrottle = _dragOcr.Lines.Count > 0 ? _dragOcr.Lines[0].Box.Height / 2 : 10;
        int throttleThreshold = Math.Max(5, snapForThrottle / 2);
        bool shouldExpand = _lastDragY == int.MinValue || Math.Abs(cur.Y - _lastDragY) >= throttleThreshold || cur.Y <= _anchorPoint.Y;
        if (shouldExpand)
        {
            _lastDragY = cur.Y;
            if (cur.Y <= _anchorPoint.Y)
            {
                // 回到锚点上方不扩展，但仍需检查捕获扩展（下方逻辑）
            }
            else
            {
                var expanded = ExpandSelectedLines(_dragOcr.Lines, _anchorPoint, cur.Y);
                if (!expanded.IsEmpty)
                {
                    PhysicalRect last;
                    lock (_dragLock) { last = _lastExpandedUnion; }
                    bool changed = !expanded.Equals(last);
                    if (changed)
                    {
                        lock (_dragLock) { _lastExpandedUnion = expanded; }
                        if (!_pendingUpdate)
                        {
                            _pendingUpdate = true;
                            try
                            {
                                _overlayService.Update(expanded, _dragMonitorId, _dragDpiX, _dragDpiY);
                            }
                            finally
                            {
                                _pendingUpdate = false;
                            }
                        }
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("Drag tick expanded to {Box} curY={CY}", expanded, cur.Y);
                    }
                }
            }
        }

        // Task4: capture expansion to 1200x1200 only vertical when drag exceeds initial frame bottom
        if (!_hasExpandedCapture && !_dragCaptureRegion.IsEmpty && cur.Y > _dragCaptureRegion.Bottom - 20 && _dragCaptureRegion.Height < 1200)
        {
            // optimistic guard to avoid concurrent re-entry; reset on failure to allow retry
            _hasExpandedCapture = true;
            try
            {
                var newSize = new PhysicalSize(1200, 1200);
                var frame = await _retryCoordinator.Capture.CaptureAroundAsync(_anchorPoint, newSize).ConfigureAwait(false);
                var newFrameRegion = frame.Region;
                var band = MakeBand(_anchorPoint, 600);
                var newOcr = await _retryCoordinator.Ocr.RecognizeAsync(frame, band).ConfigureAwait(false);
                ((IDisposable)frame).Dispose();
                lock (_dragLock)
                {
                    _dragOcr = newOcr;
                    _dragCaptureRegion = newOcr.CaptureRegion.IsEmpty ? newFrameRegion : newOcr.CaptureRegion;
                }
                if (_dragFrame != null) { ((IDisposable)_dragFrame).Dispose(); _dragFrame = null; }
                var expandedAfter = ExpandSelectedLines(newOcr.Lines, _anchorPoint, cur.Y);
                if (!expandedAfter.IsEmpty && !expandedAfter.Equals(_lastExpandedUnion))
                {
                    lock (_dragLock) { _lastExpandedUnion = expandedAfter; }
                    _overlayService.Update(expandedAfter, _dragMonitorId, _dragDpiX, _dragDpiY);
                }
            }
            catch (Exception ex)
            {
                _hasExpandedCapture = false;
                _logger.LogDebug(ex, "Drag capture expansion failed");
            }
        }
    }

    // Test helpers
    internal void SetDragStateForTest(OcrLayoutResult ocr, PhysicalPoint anchor, PhysicalRect initialUnion, MonitorId mid, uint dpiX, uint dpiY)
    {
        lock (_dragLock)
        {
            _dragOcr = ocr;
            _dragInitialUnion = initialUnion;
            _lastExpandedUnion = initialUnion;
            _anchorPoint = anchor;
            _dragMonitorId = mid;
            _dragDpiX = dpiX;
            _dragDpiY = dpiY;
            _isDragging = false;
            _hasExpandedCapture = false;
            _dragCaptureRegion = ocr.CaptureRegion;
            _dragBlock = new BlockSelectionResult(string.Join("\n", ocr.Lines.Select(l => l.Text)), initialUnion, ocr.Lines, SelectionKind.Block, Guid.NewGuid(), false);
        }
    }

    internal void SetDragFrameForTest(ScreenFrame frame)
    {
        if (_dragFrame != null) ((IDisposable)_dragFrame).Dispose();
        _dragFrame = frame;
        lock (_dragLock)
        {
            _dragCaptureRegion = frame.Region;
            _hasExpandedCapture = false;
        }
    }

    internal void HandleHoldEndForTest()
    {
        HandleHoldEnd();
    }

    internal void HandleHoldStartForTest(PhysicalPoint anchor)
    {
        _anchorPoint = anchor;
        lock (_dragLock)
        {
            _isDragging = true;
            _hasExpandedCapture = false;
            if (_dragOcr != null)
            {
                _lastExpandedUnion = ExpandSelectedLines(_dragOcr.Lines, anchor, anchor.Y);
                if (_lastExpandedUnion.IsEmpty) _lastExpandedUnion = _dragInitialUnion;
            }
            else
            {
                _lastExpandedUnion = _dragInitialUnion;
            }
        }
        _overlayService.Show(_lastExpandedUnion, _dragMonitorId, _dragDpiX, _dragDpiY, preview: true);
        _dragOverlayShownAt = Environment.TickCount64;
        StartDragTimer();
    }

    private static PhysicalRect MakeBand(PhysicalPoint anchor, int halfHeight)
    {
        return new PhysicalRect(anchor.X - 1, anchor.Y - halfHeight, 2, halfHeight * 2);
    }

    internal void TriggerDragTickForTest()
    {
        // Synchronous wrapper for tests (no WPF Dispatcher, no deadlock risk).
        // Production tick uses TriggerDragTickAsync() via Dispatcher without blocking.
        TriggerDragTickAsync().ConfigureAwait(false).GetAwaiter().GetResult();
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
        StopDragging();
        TryCancelAndClear(returnIdle: true);
        _started = false;
        _logger.LogDebug("BlockInteractionCoordinator stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _escHook.EscPressed -= OnEscPressed;
        if (_hotkeyBroker != null)
        {
            _hotkeyBroker.BlockHoldStateChanged -= OnBlockHoldStateChanged;
        }
        StopDragging();

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

    private async Task<BlockSelectionResult?> TryProbeBlockAsync(OperationSlot slot, PhysicalPoint cursor)
    {
        if (!_settings.Value.EnableSelectedTextProbe || _selectedTextProbe is null) return null;
        try
        {
            var result = await _selectedTextProbe.ProbeAsync(cursor, slot.Cts.Token).ConfigureAwait(false);
            if (result is null) return null;
            var text = SelectedTextProbePolicy.Normalize(result.Text);
            if (!SelectedTextProbePolicy.IsAdoptable(text, SelectedTextProbePolicy.BlockModeMaxChars)) return null;
            // 空间相关性校验：残留旧选区远离光标时拒绝，回退截屏/OCR
            if (!SelectedTextProbePolicy.IsSpatiallyRelevant(result.LineRects, result.UnionBox, cursor, wordMode: false))
            {
                _logger.LogDebug("[Probe] Selection not near cursor, fall back to OCR");
                return null;
            }
            return new BlockSelectionResult(text, result.UnionBox, new List<OcrLine>().AsReadOnly(), SelectionKind.Block, slot.Id, NoBlockFound: false);
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

    private async Task TranslateAndDisplayBlockAsync(OperationSlot slot, BlockSelectionResult block, MonitorId mid, uint dpiX, uint dpiY, long overlayShownAt, bool skipHold)
    {
        if (IsStaleOrCanceled(slot)) return;
        SetState(slot, AppState.Translating);
        _statusIndicator?.Update("正在翻译…");
        var translation = await _translationRouter.TranslateBlockAsync(block.BlockText!, _settings.Value.TargetLanguage, slot.Cts.Token).ConfigureAwait(false);
        if (IsStaleOrCanceled(slot)) return;
        if (!skipHold)
        {
            long elapsed = Environment.TickCount64 - overlayShownAt;
            int remaining = SelectionHoldMs - (int)elapsed;
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
        _popupService.Show(block, translation, mid, block.UnionBox, dpiX, dpiY);
        _popupService.MarkStreamCompleted();
        ScheduleSelectionAutoHide(slot);
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
        StopDragging();
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
            // 选中文本预探测放在截屏之前：若用户已显式选中文本（浏览器/编辑器等支持 UIA 的场景），
            // 可直接取段落文本跳过截屏/OCR/选择与多轮重试，显著降低延迟与识别误差。
            var probeBlock = await TryProbeBlockAsync(newSlot, anchor).ConfigureAwait(false);
            if (probeBlock != null)
            {
                if (IsStaleOrCanceled(newSlot)) return;
                unionBox = probeBlock.UnionBox;
                SetState(newSlot, AppState.OverlayVisible);
                _overlayService.Show(probeBlock.UnionBox, mid, dpiX, dpiY);
                long overlayShownAtProbe = Environment.TickCount64;
                if (IsStaleOrCanceled(newSlot)) return;
                await TranslateAndDisplayBlockAsync(newSlot, probeBlock, mid, dpiX, dpiY, overlayShownAtProbe, skipHold: true).ConfigureAwait(false);
                return;
            }
            if (IsStaleOrCanceled(newSlot)) return;

            var (ocr, block, captures) = await _retryCoordinator.SelectBlockWithRetryAsync(
                anchor, mid, dpiX, dpiY, newSlot.Cts.Token).ConfigureAwait(false);

            if (IsStaleOrCanceled(newSlot)) return;

            // Store for potential drag expansion (Task2: hold-drag uses already OCRed lines)
            lock (_dragLock)
            {
                _dragOcr = ocr;
                _dragBlock = block;
                _dragInitialUnion = block.UnionBox;
                _lastExpandedUnion = block.UnionBox;
                _dragCaptureRegion = ocr.CaptureRegion;
                _hasExpandedCapture = false;
                _anchorPoint = anchor;
                _dragMonitorId = mid;
                _dragDpiX = dpiX;
                _dragDpiY = dpiY;
            }

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

            await TranslateAndDisplayBlockAsync(newSlot, block, mid, dpiX, dpiY, overlayShownAt, skipHold: false).ConfigureAwait(false);
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
