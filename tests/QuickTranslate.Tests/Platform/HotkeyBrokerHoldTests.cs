using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Platform.Hotkeys;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class HotkeyBrokerHoldTests
{
    [Fact]
    public async Task Block_HoldOver400ms_FiresHoldStartThenHoldEnd()
    {
        var svc = new FakeGlobalHotkeyService();
        var lifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(svc, lifecycle, logger);
        int holdStarts = 0, holdEnds = 0, fires = 0;
        broker.BlockHoldStateChanged += (s, e) => { if (e.Phase == HotkeyHoldPhase.Start) holdStarts++; if (e.Phase == HotkeyHoldPhase.End) holdEnds++; };
        broker.HotkeyFired += (s, e) => fires++;

        svc.RaiseKeyDown(DefaultHotkeyBroker.BlockId);
        await Task.Delay(450);
        svc.RaiseKeyUp(DefaultHotkeyBroker.BlockId);
        await Task.Delay(80);

        Assert.Equal(1, holdStarts);
        Assert.Equal(1, holdEnds);
        Assert.Equal(0, fires);
    }

    [Fact]
    public async Task Block_TapUnder400ms_FiresHotkeyFiredOnly()
    {
        var svc = new FakeGlobalHotkeyService();
        var lifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(svc, lifecycle, logger);
        int holdStarts = 0, holdEnds = 0, fires = 0;
        HotkeyEvent? firedEvent = null;
        broker.BlockHoldStateChanged += (s, e) => { if (e.Phase == HotkeyHoldPhase.Start) holdStarts++; if (e.Phase == HotkeyHoldPhase.End) holdEnds++; };
        broker.HotkeyFired += (s, e) => { fires++; firedEvent = e; };

        svc.RaiseKeyDown(DefaultHotkeyBroker.BlockId);
        await Task.Delay(200);
        svc.RaiseKeyUp(DefaultHotkeyBroker.BlockId);
        await Task.Delay(80);

        Assert.Equal(0, holdStarts);
        Assert.Equal(0, holdEnds);
        Assert.Equal(1, fires);
        Assert.NotNull(firedEvent);
        Assert.Equal(HotkeyEventType.Block, firedEvent!.Value.Type);
    }

    [Fact]
    public async Task Block_HoldEndDuration_IsAtLeast400ms()
    {
        var svc = new FakeGlobalHotkeyService();
        var lifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(svc, lifecycle, logger);
        TimeSpan? endDuration = null;
        broker.BlockHoldStateChanged += (s, e) => { if (e.Phase == HotkeyHoldPhase.End) endDuration = e.HoldDuration; };

        svc.RaiseKeyDown(DefaultHotkeyBroker.BlockId);
        await Task.Delay(450);
        svc.RaiseKeyUp(DefaultHotkeyBroker.BlockId);
        await Task.Delay(80);

        Assert.NotNull(endDuration);
        Assert.True(endDuration!.Value.TotalMilliseconds >= 400, $"HoldDuration {endDuration.Value.TotalMilliseconds} should be >=400ms");
        Assert.True(endDuration!.Value.TotalMilliseconds < 2000, $"HoldDuration {endDuration.Value.TotalMilliseconds} should be <2000ms");
    }

    [Fact]
    public async Task Block_Hold_DoesNotFireViaWmHotkeyDuplicate()
    {
        var svc = new FakeGlobalHotkeyService();
        var lifecycle = new FakeAppLifecycle();
        var logger = NullLogger<DefaultHotkeyBroker>.Instance;
        var broker = new DefaultHotkeyBroker(svc, lifecycle, logger);
        int fires = 0;
        broker.HotkeyFired += (s, e) => fires++;
        // Simulate WM_HOTKEY arriving during hold (should be suppressed)
        svc.RaiseKeyDown(DefaultHotkeyBroker.BlockId);
        await Task.Delay(50);
        svc.RaiseHotkeyPressed(DefaultHotkeyBroker.BlockId);
        await Task.Delay(400);
        svc.RaiseKeyUp(DefaultHotkeyBroker.BlockId);
        await Task.Delay(80);
        // Should have 0 fires because hold path owns Block
        Assert.Equal(0, fires);
    }
}
