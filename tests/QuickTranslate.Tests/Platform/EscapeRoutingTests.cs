using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Platform.Hotkeys;
using QuickTranslate.Platform.UnmanagedMethods;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class EscapeRoutingTests
{
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _queue.Enqueue(() => callback(state));
            Ready.TrySetResult();
        }
        public void Drain() { while (_queue.TryDequeue(out var action)) action(); }
    }

    [Fact]
    public async Task Escape_DiscardsHoldStartQueuedBeforeCancellation()
    {
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        var source = new FakeGlobalHotkeyService();
        DefaultHotkeyBroker broker;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            broker = new DefaultHotkeyBroker(source, new FakeAppLifecycle(),
                NullLogger<DefaultHotkeyBroker>.Instance);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        using (broker)
        {
            var emitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            broker.BlockHoldStateChanged += (_, _) => emitted.TrySetResult();
            source.RaiseKeyDown(DefaultHotkeyBroker.BlockId);
            await Task.WhenAny(context.Ready.Task, emitted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(context.Ready.Task.IsCompleted, "HoldStart must be delivered on the owning event context.");
            source.RaiseHotkeyPressed(DefaultHotkeyBroker.EscId);
            context.Drain();
            Assert.False(emitted.Task.IsCompleted);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Escape_CancelsPendingBlockPress_WithoutRestartingOnHoldOrRelease(bool releaseImmediately)
    {
        var source = new FakeGlobalHotkeyService();
        using var broker = new DefaultHotkeyBroker(source, new FakeAppLifecycle(),
            NullLogger<DefaultHotkeyBroker>.Instance, new DefaultEscHook());
        var fired = new List<HotkeyEventType>();
        var holds = new List<HotkeyHoldPhase>();
        broker.HotkeyFired += (_, e) => fired.Add(e.Type);
        broker.BlockHoldStateChanged += (_, e) => holds.Add(e.Phase);

        source.RaiseKeyDown(DefaultHotkeyBroker.BlockId);
        source.RaiseHotkeyPressed(DefaultHotkeyBroker.EscId);
        if (releaseImmediately) source.RaiseKeyUp(DefaultHotkeyBroker.BlockId);
        await Task.Delay(550);
        if (!releaseImmediately) source.RaiseKeyUp(DefaultHotkeyBroker.BlockId);

        Assert.Equal(new[] { HotkeyEventType.Escape }, fired);
        Assert.Empty(holds);

        // A fresh press after cancellation must still work.
        source.RaiseKeyDown(DefaultHotkeyBroker.BlockId);
        source.RaiseKeyUp(DefaultHotkeyBroker.BlockId);
        Assert.Equal(new[] { HotkeyEventType.Escape, HotkeyEventType.Block }, fired);
    }

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
