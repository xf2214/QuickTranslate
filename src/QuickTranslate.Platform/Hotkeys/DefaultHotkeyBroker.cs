using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Common;
using QuickTranslate.Core.Options;

namespace QuickTranslate.Platform.Hotkeys;

public class DefaultHotkeyBroker : IHotkeyBroker
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

    public event EventHandler<HotkeyEvent>? HotkeyFired;

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
}
