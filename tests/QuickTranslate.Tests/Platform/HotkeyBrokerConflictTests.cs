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

    /// <summary>
    /// 关键修复：Probe 对已经自己注册过的组合必须返回 true，
    /// 否则默认 Alt+1 / Alt+2 会被误判为冲突。
    /// </summary>
    [Fact]
    public void Probe_SelfRegisteredWordCombo_ReturnsTrue_NoSpuriousConflict()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        // 模拟 Win32 真实行为：同句柄下同组合换 ID 注册会失败
        fakeHotkey.RegisterFunc = (id, mods, key) =>
        {
            // probeId=0xBFFF 使用同句柄，遇到 WordId 已注册组合必失败
            if (id == 0xBFFF && mods == HotkeyModifiers.Alt && key == KeyboardKey.D1)
                return false;
            return true;
        };
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);

        broker.RegisterDefaultsFromSettings(new AppSettings());

        bool probe = broker.Probe(HotkeyModifiers.Alt, KeyboardKey.D1);

        Assert.True(probe, "自身已注册的 Word 组合不应被 Probe 误判为冲突");
    }

    [Fact]
    public void Probe_SelfRegisteredBlockCombo_ReturnsTrue()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        fakeHotkey.RegisterFunc = (id, mods, key) =>
        {
            if (id == 0xBFFF && mods == HotkeyModifiers.Alt && key == KeyboardKey.D2)
                return false;
            return true;
        };
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);

        broker.RegisterDefaultsFromSettings(new AppSettings());

        bool probe = broker.Probe(HotkeyModifiers.Alt, KeyboardKey.D2);

        Assert.True(probe, "自身已注册的 Block 组合不应被 Probe 误判为冲突");
    }

    [Fact]
    public void Probe_ExternalCombo_ReallyOccupied_ReturnsFalse()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        // 外部占用：任何 ID（包括 probeId）注册 Ctrl+C 都失败
        fakeHotkey.RegisterFunc = (id, mods, key) =>
        {
            if (mods == HotkeyModifiers.Ctrl && key == KeyboardKey.C)
                return false;
            return true;
        };
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);

        broker.RegisterDefaultsFromSettings(new AppSettings());

        bool probe = broker.Probe(HotkeyModifiers.Ctrl, KeyboardKey.C);

        Assert.False(probe, "外部占用的 Ctrl+C 应被正确识别为冲突");
    }

    [Fact]
    public void Probe_FreeCombo_ReturnsTrue()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        fakeHotkey.RegisterReturns = true;
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);

        broker.RegisterDefaultsFromSettings(new AppSettings());

        bool probe = broker.Probe(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, KeyboardKey.W);

        Assert.True(probe);
        // probeId 必须被及时清理
        Assert.Contains(0xBFFF, fakeHotkey.UnregisterCalls);
    }
}
