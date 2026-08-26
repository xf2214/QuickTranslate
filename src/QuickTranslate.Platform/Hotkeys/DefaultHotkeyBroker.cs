using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Common;
using QuickTranslate.Core.Options;

namespace QuickTranslate.Platform.Hotkeys;

public class DefaultHotkeyBroker : IHotkeyBroker, IDisposable
{
    public const int WordId = 0xB001;
    public const int BlockId = 0xB002;
    public const int EscId = 0xB003;

    private readonly IGlobalHotkeyService _globalHotkeyService;
    private readonly IAppLifecycle _appLifecycle;
    private readonly ILogger<DefaultHotkeyBroker> _logger;
    private readonly IEscHook? _escHook;

    private HotkeyCombo? _wordCombo;
    private HotkeyCombo? _blockCombo;
    private HotkeyCombo? _escCombo;

    private const int HoldThresholdMs = 400;
    private CancellationTokenSource? _holdCts;
    private DateTimeOffset? _blockDownTimestamp;
    private bool _holdStarted;
    private readonly object _holdLock = new();
    private bool _disposed;

    public event EventHandler<HotkeyEvent>? HotkeyFired;
    public event EventHandler<HotkeyHoldEventArgs>? BlockHoldStateChanged;

    public DefaultHotkeyBroker(
        IGlobalHotkeyService globalHotkeyService,
        IAppLifecycle appLifecycle,
        ILogger<DefaultHotkeyBroker> logger,
        IEscHook? escHook = null)
    {
        _globalHotkeyService = globalHotkeyService;
        _appLifecycle = appLifecycle;
        _logger = logger;
        _escHook = escHook;

        _globalHotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        _globalHotkeyService.KeyStateChanged += OnKeyStateChanged;
    }

    public void RegisterDefaultsFromSettings(AppSettings settings)
    {
        UnregisterAll();

        if (!Register(WordId, settings.WordHotkey))
        {
            _logger.LogWarning("Failed to register Word hotkey: {Combo}", settings.WordHotkey);
        }
        else
        {
            _wordCombo = settings.WordHotkey;
        }

        if (!Register(BlockId, settings.BlockHotkey))
        {
            _logger.LogWarning("Failed to register Block hotkey: {Combo}", settings.BlockHotkey);
        }
        else
        {
            _blockCombo = settings.BlockHotkey;
        }

        // 注意：Escape 键不能通过 RegisterHotKey 无修饰符注册（Win32 API 系统直接拒绝）。
        // 取消操作由 IEscHook（低级键盘钩子）独立触发，这里不再尝试注册 Esc 全局热键。
        _escCombo = null;
    }

    public Result TryUpdateHotkeys(HotkeyCombo newWord, HotkeyCombo newBlock)
    {
        var oldWord = _wordCombo;
        var oldBlock = _blockCombo;

        _globalHotkeyService.Unregister(WordId);
        _globalHotkeyService.Unregister(BlockId);

        if (!Register(WordId, newWord))
        {
            try
            {
                if (oldWord != null)
                    Register(WordId, oldWord);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rollback Word hotkey during failed update");
            }
            if (oldBlock != null)
            {
                try { Register(BlockId, oldBlock); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to rollback Block hotkey after Word failed"); }
            }
            return Result.Fail($"Word hotkey conflict: {newWord}");
        }

        if (!Register(BlockId, newBlock))
        {
            _globalHotkeyService.Unregister(WordId);
            try
            {
                if (oldWord != null)
                    Register(WordId, oldWord);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rollback Word hotkey after Block failed");
            }
            try
            {
                if (oldBlock != null)
                    Register(BlockId, oldBlock);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rollback Block hotkey after Block failed");
            }
            return Result.Fail($"Block hotkey conflict: {newBlock}");
        }

        _wordCombo = newWord;
        _blockCombo = newBlock;
        return Result.Ok();
    }

    public void UnregisterAll()
    {
        try { _globalHotkeyService.Unregister(WordId); } catch (Exception ex) { _logger.LogDebug(ex, "[HotkeyBroker.UnregisterAll] Unregister WordId {Id} failed [ErrorCode=HOTKEY_UNREGISTER_FAIL]", WordId); }
        try { _globalHotkeyService.Unregister(BlockId); } catch (Exception ex) { _logger.LogDebug(ex, "[HotkeyBroker.UnregisterAll] Unregister BlockId {Id} failed [ErrorCode=HOTKEY_UNREGISTER_FAIL]", BlockId); }
        try { _globalHotkeyService.Unregister(EscId); } catch (Exception ex) { _logger.LogDebug(ex, "[HotkeyBroker.UnregisterAll] Unregister EscId {Id} failed [ErrorCode=HOTKEY_UNREGISTER_FAIL]", EscId); }
        lock (_holdLock)
        {
            try { _holdCts?.Cancel(); } catch { }
            _holdCts?.Dispose();
            _holdCts = null;
            _blockDownTimestamp = null;
            _holdStarted = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _globalHotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed; } catch { }
        try { _globalHotkeyService.KeyStateChanged -= OnKeyStateChanged; } catch { }
        UnregisterAll();
        GC.SuppressFinalize(this);
    }

    public bool Probe(HotkeyModifiers mods, KeyboardKey key)
    {
        const int probeId = 0xBFFF;
        try
        {
            // 自身已注册的组合不视为冲突（Win32 RegisterHotKey 同句柄下同组合+异ID必失败）
            if (_wordCombo != null &&
                _wordCombo.Modifiers == mods &&
                _wordCombo.Key == key)
            {
                return true;
            }
            if (_blockCombo != null &&
                _blockCombo.Modifiers == mods &&
                _blockCombo.Key == key)
            {
                return true;
            }
            if (_escCombo != null &&
                _escCombo.Modifiers == mods &&
                _escCombo.Key == key)
            {
                return true;
            }

            bool ok = _globalHotkeyService.Register(probeId, mods, key);
            if (ok)
            {
                _globalHotkeyService.Unregister(probeId);
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HotkeyBroker.Probe] Probe failed for {Mods}+{Key} [ErrorCode=HOTKEY_PROBE_FAIL]", mods, key);
            return false;
        }
    }

    private bool Register(int id, HotkeyCombo c)
    {
        return _globalHotkeyService.Register(id, c.Modifiers, c.Key);
    }

    private void OnGlobalHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        HotkeyEventType? eventType = null;

        switch (e.Id)
        {
            case WordId:
                if (_appLifecycle.IsPaused)
                {
                    _logger.LogDebug("hotkey suppressed by paused");
                    return;
                }
                eventType = HotkeyEventType.Word;
                break;
            case BlockId:
                if (_appLifecycle.IsPaused)
                {
                    _logger.LogDebug("hotkey suppressed by paused");
                    return;
                }
                lock (_holdLock)
                {
                    if (_blockDownTimestamp != null || _holdCts != null || _holdStarted)
                    {
                        _logger.LogDebug("Block WM_HOTKEY suppressed by hold tracking");
                        return;
                    }
                }
                eventType = HotkeyEventType.Block;
                break;
            case EscId:
                eventType = HotkeyEventType.Escape;
                break;
        }

        if (!eventType.HasValue)
            return;

        var hotkeyEvent = new HotkeyEvent(eventType.Value, DateTimeOffset.Now);
        HotkeyFired?.Invoke(this, hotkeyEvent);

        if (eventType.Value == HotkeyEventType.Escape)
        {
            _escHook?.RaiseEscPressed();
        }
    }

    private void OnKeyStateChanged(object? sender, KeyStateChangedEventArgs e)
    {
        if (e.Id != BlockId)
            return;

        if (e.Phase == KeyStatePhase.Down)
        {
            HandleBlockDown(e);
        }
        else if (e.Phase == KeyStatePhase.Up)
        {
            HandleBlockUp(e);
        }
    }

    private void HandleBlockDown(KeyStateChangedEventArgs e)
    {
        if (_appLifecycle.IsPaused)
        {
            _logger.LogDebug("Block hold down suppressed by paused");
            return;
        }

        CancellationTokenSource? cts;
        lock (_holdLock)
        {
            if (_blockDownTimestamp != null && _holdCts != null)
                return;
            _blockDownTimestamp = e.Timestamp;
            _holdStarted = false;
            _holdCts?.Cancel();
            _holdCts?.Dispose();
            _holdCts = new CancellationTokenSource();
            cts = _holdCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(HoldThresholdMs, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            bool shouldFire = false;
            lock (_holdLock)
            {
                if (cts.IsCancellationRequested)
                    return;
                if (_holdStarted)
                    return;
                // Still holding
                _holdStarted = true;
                shouldFire = true;
            }

            if (shouldFire)
            {
                var holdArgs = new HotkeyHoldEventArgs(HotkeyEventType.Block, HotkeyHoldPhase.Start, TimeSpan.FromMilliseconds(HoldThresholdMs), DateTimeOffset.Now, BlockId);
                BlockHoldStateChanged?.Invoke(this, holdArgs);
                _logger.LogDebug("Block hold started after {Threshold}ms", HoldThresholdMs);
            }
        });
    }

    private void HandleBlockUp(KeyStateChangedEventArgs e)
    {
        CancellationTokenSource? ctsToCancel = null;
        bool wasHoldStarted;
        DateTimeOffset? downTimestamp;
        TimeSpan duration;

        lock (_holdLock)
        {
            downTimestamp = _blockDownTimestamp;
            if (downTimestamp == null)
                return;
            wasHoldStarted = _holdStarted;
            duration = e.HoldDuration ?? (e.Timestamp - downTimestamp.Value);
            ctsToCancel = _holdCts;
            _holdCts = null;
            // keep _blockDownTimestamp and _holdStarted until after firing, but capture values
        }

        try { ctsToCancel?.Cancel(); } catch { }
        ctsToCancel?.Dispose();

        if (wasHoldStarted)
        {
            var holdEnd = new HotkeyHoldEventArgs(HotkeyEventType.Block, HotkeyHoldPhase.End, duration, DateTimeOffset.Now, BlockId);
            BlockHoldStateChanged?.Invoke(this, holdEnd);
            _logger.LogDebug("Block hold ended duration {Duration}ms", duration.TotalMilliseconds);
        }
        else
        {
            if (_appLifecycle.IsPaused)
            {
                _logger.LogDebug("Block tap suppressed by paused");
            }
            else
            {
                var hotkeyEvent = new HotkeyEvent(HotkeyEventType.Block, DateTimeOffset.Now, duration, false);
                HotkeyFired?.Invoke(this, hotkeyEvent);
                _logger.LogDebug("Block tap fired duration {Duration}ms", duration.TotalMilliseconds);
            }
        }

        lock (_holdLock)
        {
            _blockDownTimestamp = null;
            _holdStarted = false;
        }
    }
}
