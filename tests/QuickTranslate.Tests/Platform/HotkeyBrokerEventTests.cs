using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Platform.Hotkeys;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class FakeGlobalHotkeyService : IGlobalHotkeyService
{
    public List<(int Id, HotkeyModifiers Modifiers, KeyboardKey Key)> RegisterCalls { get; } = new();
    public List<int> UnregisterCalls { get; } = new();
    public List<int> UnregisterAllCalls { get; } = new();

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public bool RegisterReturns { get; set; } = true;
    public Func<int, HotkeyModifiers, KeyboardKey, bool>? RegisterFunc { get; set; }

    public bool Register(int id, HotkeyModifiers modifiers, KeyboardKey key)
    {
        RegisterCalls.Add((id, modifiers, key));
        return RegisterFunc != null ? RegisterFunc(id, modifiers, key) : RegisterReturns;
    }

    public void Unregister(int id)
    {
        UnregisterCalls.Add(id);
    }

    public void UnregisterAll()
    {
        UnregisterAllCalls.Add(1);
    }

    public void RaiseHotkeyPressed(int id)
    {
        HotkeyPressed?.Invoke(this, new HotkeyEventArgs(id, HotkeyModifiers.None, KeyboardKey.None));
    }
}

public class FakeAppLifecycle : IAppLifecycle
{
    public bool IsPaused { get; private set; }

    public event EventHandler? Paused;
    public event EventHandler? Resumed;
    public event EventHandler<int>? ShuttingDown;

    public void Pause()
    {
        IsPaused = true;
        Paused?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        IsPaused = false;
        Resumed?.Invoke(this, EventArgs.Empty);
    }

    public void Shutdown(int exitCode = 0)
    {
        ShuttingDown?.Invoke(this, exitCode);
    }
}

public class FakeInteractionCoordinator : IInteractionCoordinator
{
    public List<HotkeyEvent> HandleHotkeyCalls { get; } = new();

    public void HandleHotkey(HotkeyEvent hotkeyEvent)
    {
        HandleHotkeyCalls.Add(hotkeyEvent);
    }

    public void ShowTranslationPopup(string sourceText, string translatedText) { }
    public void ShowSettingsWindow() { }
}

public class HotkeyBrokerEventTests
{
    [Fact]
    public void RaiseHotkeyPressed_WordId_CoordinatorReceivesWord()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var coordinator = new FakeInteractionCoordinator();
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);
        broker.HotkeyFired += (s, e) => coordinator.HandleHotkey(e);

        fakeHotkey.RaiseHotkeyPressed(DefaultHotkeyBroker.WordId);

        Assert.Single(coordinator.HandleHotkeyCalls);
        Assert.Equal(HotkeyEventType.Word, coordinator.HandleHotkeyCalls[0].Type);
    }

    [Fact]
    public void RaiseHotkeyPressed_BlockId_CoordinatorReceivesBlock()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var coordinator = new FakeInteractionCoordinator();
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);
        broker.HotkeyFired += (s, e) => coordinator.HandleHotkey(e);

        fakeHotkey.RaiseHotkeyPressed(DefaultHotkeyBroker.BlockId);

        Assert.Single(coordinator.HandleHotkeyCalls);
        Assert.Equal(HotkeyEventType.Block, coordinator.HandleHotkeyCalls[0].Type);
    }

    [Fact]
    public void RaiseHotkeyPressed_EscId_CoordinatorReceivesEscape()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var coordinator = new FakeInteractionCoordinator();
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);
        broker.HotkeyFired += (s, e) => coordinator.HandleHotkey(e);

        fakeHotkey.RaiseHotkeyPressed(DefaultHotkeyBroker.EscId);

        Assert.Single(coordinator.HandleHotkeyCalls);
        Assert.Equal(HotkeyEventType.Escape, coordinator.HandleHotkeyCalls[0].Type);
    }

    [Fact]
    public void Paused_RaiseHotkeyPressed_WordId_CoordinatorReceivesZero()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var coordinator = new FakeInteractionCoordinator();
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);
        broker.HotkeyFired += (s, e) => coordinator.HandleHotkey(e);

        fakeLifecycle.Pause();
        fakeHotkey.RaiseHotkeyPressed(DefaultHotkeyBroker.WordId);

        Assert.Empty(coordinator.HandleHotkeyCalls);
    }

    [Fact]
    public void Paused_RaiseHotkeyPressed_EscId_CoordinatorStillReceivesEscape()
    {
        var fakeHotkey = new FakeGlobalHotkeyService();
        var fakeLifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var coordinator = new FakeInteractionCoordinator();
        var broker = new DefaultHotkeyBroker(fakeHotkey, fakeLifecycle, logger);
        broker.HotkeyFired += (s, e) => coordinator.HandleHotkey(e);

        fakeLifecycle.Pause();
        fakeHotkey.RaiseHotkeyPressed(DefaultHotkeyBroker.EscId);

        Assert.Single(coordinator.HandleHotkeyCalls);
        Assert.Equal(HotkeyEventType.Escape, coordinator.HandleHotkeyCalls[0].Type);
    }
}
