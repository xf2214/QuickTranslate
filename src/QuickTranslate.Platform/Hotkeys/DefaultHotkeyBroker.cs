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

        var escCombo = new HotkeyCombo(HotkeyModifiers.None, KeyboardKey.Escape);
        if (!Register(EscId, escCombo))
        {
            _logger.LogWarning("Failed to register Escape hotkey: {Combo}", escCombo);
        }
        else
        {
            _escCombo = escCombo;
        }
    }

    public Result TryUpdateHotkeys(HotkeyCombo newWord, HotkeyCombo newBlock)
    {
        var oldWord = _wordCombo;
        var oldBlock = _blockCombo;

        _globalHotkeyService.Unregister(WordId);
        _globalHotkeyService.Unregister(BlockId);

        if (!Register(WordId, newWord))
        {
            if (oldWord != null)
                Register(WordId, oldWord);
            return Result.Fail($"Word hotkey conflict: {newWord}");
        }

        if (!Register(BlockId, newBlock))
        {
            _globalHotkeyService.Unregister(WordId);
            if (oldWord != null)
                Register(WordId, oldWord);
            if (oldBlock != null)
                Register(BlockId, oldBlock);
            return Result.Fail($"Block hotkey conflict: {newBlock}");
        }

        _wordCombo = newWord;
        _blockCombo = newBlock;
        return Result.Ok();
    }

    public void UnregisterAll()
    {
        try { _globalHotkeyService.Unregister(WordId); } catch { }
        try { _globalHotkeyService.Unregister(BlockId); } catch { }
        try { _globalHotkeyService.Unregister(EscId); } catch { }
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
