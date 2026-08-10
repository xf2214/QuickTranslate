using QuickTranslate.Platform.Tray;
using Xunit;

namespace QuickTranslate.Tests.App;

public class AppLifecycleTests
{
    [Fact]
    public void Pause_SetsIsPausedTrue_AndRaisesPausedEvent()
    {
        int shutdownCalledWith = -1;
        var lifecycle = new DefaultAppLifecycle(code => { shutdownCalledWith = code; });
        var pausedRaised = false;
        var resumedRaised = false;
        var shuttingDownRaised = false;

        lifecycle.Paused += (s, e) => pausedRaised = true;
        lifecycle.Resumed += (s, e) => resumedRaised = true;
        lifecycle.ShuttingDown += (s, e) => shuttingDownRaised = true;

        lifecycle.Pause();

        Assert.True(lifecycle.IsPaused);
        Assert.True(pausedRaised);
        Assert.False(resumedRaised);
        Assert.False(shuttingDownRaised);
        Assert.Equal(-1, shutdownCalledWith);
    }

    [Fact]
    public void Resume_SetsIsPausedFalse_AndRaisesResumedEvent()
    {
        int shutdownCalledWith = -1;
        var lifecycle = new DefaultAppLifecycle(code => { shutdownCalledWith = code; });
        var pausedRaised = false;
        var resumedRaised = false;

        lifecycle.Paused += (s, e) => pausedRaised = true;
        lifecycle.Resumed += (s, e) => resumedRaised = true;

        lifecycle.Pause();
        Assert.True(lifecycle.IsPaused);
        Assert.True(pausedRaised);
        Assert.False(resumedRaised);

        pausedRaised = false;
        lifecycle.Resume();

        Assert.False(lifecycle.IsPaused);
        Assert.False(pausedRaised);
        Assert.True(resumedRaised);
        Assert.Equal(-1, shutdownCalledWith);
    }

    [Fact]
    public void Shutdown_RaisesShuttingDownEvent_WithExitCode_AndInvokesDelegate()
    {
        int shutdownCalledWith = -1;
        var lifecycle = new DefaultAppLifecycle(code => { shutdownCalledWith = code; });
        int shuttingDownCode = -1;
        var pausedRaised = false;
        var resumedRaised = false;

        lifecycle.Paused += (s, e) => pausedRaised = true;
        lifecycle.Resumed += (s, e) => resumedRaised = true;
        lifecycle.ShuttingDown += (s, e) => { shuttingDownCode = e; };

        lifecycle.Shutdown(42);

        Assert.False(lifecycle.IsPaused);
        Assert.False(pausedRaised);
        Assert.False(resumedRaised);
        Assert.Equal(42, shuttingDownCode);
        Assert.Equal(42, shutdownCalledWith);
    }

    [Fact]
    public void Shutdown_DefaultExitCode_Zero()
    {
        int shutdownCalledWith = -1;
        var lifecycle = new DefaultAppLifecycle(code => { shutdownCalledWith = code; });
        int shuttingDownCode = -1;
        lifecycle.ShuttingDown += (s, e) => { shuttingDownCode = e; };

        lifecycle.Shutdown();

        Assert.Equal(0, shuttingDownCode);
        Assert.Equal(0, shutdownCalledWith);
    }

    [Fact]
    public void Pause_Idempotent_DoesNotRaiseEventTwice()
    {
        var lifecycle = new DefaultAppLifecycle(_ => { });
        int pausedCount = 0;
        lifecycle.Paused += (s, e) => pausedCount++;

        lifecycle.Pause();
        lifecycle.Pause();
        lifecycle.Pause();

        Assert.True(lifecycle.IsPaused);
        Assert.Equal(1, pausedCount);
    }

    [Fact]
    public void Resume_Idempotent_DoesNotRaiseEventTwice()
    {
        var lifecycle = new DefaultAppLifecycle(_ => { });
        int resumedCount = 0;
        lifecycle.Resumed += (s, e) => resumedCount++;

        lifecycle.Resume();
        lifecycle.Resume();

        Assert.False(lifecycle.IsPaused);
        Assert.Equal(0, resumedCount);

        lifecycle.Pause();
        resumedCount = 0;
        lifecycle.Resume();
        lifecycle.Resume();

        Assert.False(lifecycle.IsPaused);
        Assert.Equal(1, resumedCount);
    }
}
