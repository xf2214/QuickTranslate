using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Platform.Hotkeys;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class HotkeyBrokerConflictTests
{
    [Fact]
    public void TryUpdateHotkeys_WordConflict_ReturnsFailAndRollsBack()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);

        var settings = new AppSettings();
        broker.RegisterDefaultsFromSettings(settings);

        fakeHotkey.RegisterCalls.Clear();
        fakeHotkey.UnregisterCalls.Clear();

        int registerCallCount = 0;
        fakeHotkey.RegisterFunc = (id, mods, key) =>
        {
            registerCallCount++;
            if (id == DefaultHotkeyBroker.WordId && registerCallCount == 1)
                return false;
            return true;
        };

        var newWord = new HotkeyCombo(HotkeyModifiers.Ctrl, KeyboardKey.D3);
        var newBlock = new HotkeyCombo(HotkeyModifiers.Ctrl, KeyboardKey.D4);

        var result = broker.TryUpdateHotkeys(newWord, newBlock);

        Assert.False(result.IsSuccess);
        Assert.Contains("Word hotkey conflict", result.ErrorMessage);

        var registerIds = fakeHotkey.RegisterCalls.Select(c => c.Id).ToList();

        Assert.Contains(DefaultHotkeyBroker.WordId, fakeHotkey.UnregisterCalls);
        Assert.Contains(DefaultHotkeyBroker.BlockId, fakeHotkey.UnregisterCalls);

        int wordNewIdx = registerIds.IndexOf(DefaultHotkeyBroker.WordId);
        int wordRollbackIdx = registerIds.LastIndexOf(DefaultHotkeyBroker.WordId);
        Assert.True(wordRollbackIdx > wordNewIdx, "Expected WordId to be registered twice: new (failed) then rollback");
    }

    [Fact]
    public void TryUpdateHotkeys_BlockConflict_ReturnsFailAndRollsBack()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);

        var settings = new AppSettings();
        broker.RegisterDefaultsFromSettings(settings);

        fakeHotkey.RegisterCalls.Clear();
        fakeHotkey.UnregisterCalls.Clear();

        int registerCallCount = 0;
        fakeHotkey.RegisterFunc = (id, mods, key) =>
        {
            registerCallCount++;
            if (id == DefaultHotkeyBroker.BlockId && registerCallCount == 2)
                return false;
            return true;
        };

        var newWord = new HotkeyCombo(HotkeyModifiers.Ctrl, KeyboardKey.D3);
        var newBlock = new HotkeyCombo(HotkeyModifiers.Ctrl, KeyboardKey.D4);

        var result = broker.TryUpdateHotkeys(newWord, newBlock);

        Assert.False(result.IsSuccess);
        Assert.Contains("Block hotkey conflict", result.ErrorMessage);

        var registerIds = fakeHotkey.RegisterCalls.Select(c => c.Id).ToList();

        Assert.Contains(DefaultHotkeyBroker.WordId, fakeHotkey.UnregisterCalls);
        Assert.Contains(DefaultHotkeyBroker.BlockId, fakeHotkey.UnregisterCalls);

        int blockNewIdx = registerIds.IndexOf(DefaultHotkeyBroker.BlockId);
        int wordRollbackIdx = registerIds.LastIndexOf(DefaultHotkeyBroker.WordId);
        int blockRollbackIdx = registerIds.LastIndexOf(DefaultHotkeyBroker.BlockId);
        Assert.True(blockRollbackIdx > blockNewIdx, "Expected BlockId rollback register after failed new register");
        Assert.True(wordRollbackIdx > blockNewIdx, "Expected WordId rollback register after Block failed");
    }
}
