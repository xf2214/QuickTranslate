using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Platform.Hotkeys;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class EscapeRoutingTests
{
    [Fact]
    public void KeyboardEscape_DownEmitsOnceUntilKeyUp()
    {
        var tracker = new EscapeKeyState();

        Assert.False(tracker.ShouldEmit(-1, User32.WM_KEYDOWN, (int)KeyboardKey.Escape));
        Assert.False(tracker.ShouldEmit(0, User32.WM_KEYDOWN, (int)KeyboardKey.D1));
        Assert.True(tracker.ShouldEmit(0, User32.WM_KEYDOWN, (int)KeyboardKey.Escape));
        Assert.False(tracker.ShouldEmit(0, User32.WM_SYSKEYDOWN, (int)KeyboardKey.Escape));
        Assert.False(tracker.ShouldEmit(0, User32.WM_KEYUP, (int)KeyboardKey.Escape));
        Assert.True(tracker.ShouldEmit(0, User32.WM_SYSKEYDOWN, (int)KeyboardKey.Escape));
        Assert.False(tracker.ShouldEmit(0, User32.WM_SYSKEYUP, (int)KeyboardKey.Escape));
        Assert.True(tracker.ShouldEmit(0, User32.WM_KEYDOWN, (int)KeyboardKey.Escape));
        tracker.Reset();
        Assert.True(tracker.ShouldEmit(0, User32.WM_KEYDOWN, (int)KeyboardKey.Escape));
    }

    [Fact]
    public void EscapeHotkey_PropagatesToCoordinatorAndEscHook()
    {
        var source = new FakeGlobalHotkeyService();
        var escHook = new DefaultEscHook();
        using var broker = new DefaultHotkeyBroker(
            source,
            new FakeAppLifecycle(),
            NullLogger<DefaultHotkeyBroker>.Instance,
            escHook);

        var coordinatorEvents = new List<HotkeyEventType>();
        var hookCount = 0;
        broker.HotkeyFired += (_, e) => coordinatorEvents.Add(e.Type);
        escHook.EscPressed += (_, _) => hookCount++;

        source.RaiseHotkeyPressed(DefaultHotkeyBroker.EscId);

        Assert.Equal(new[] { HotkeyEventType.Escape }, coordinatorEvents);
        Assert.Equal(1, hookCount);
    }
}
