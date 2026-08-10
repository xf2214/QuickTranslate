using WpfApplication = System.Windows.Application;
using QuickTranslate.Core.Abstractions;

namespace QuickTranslate.Platform.Tray;

public class DefaultAppLifecycle : IAppLifecycle
{
    private bool _isPaused;
    private readonly Action<int>? _shutdownDelegate;

    public bool IsPaused => _isPaused;

    public event EventHandler? Paused;
    public event EventHandler? Resumed;
    public event EventHandler<int>? ShuttingDown;

    public DefaultAppLifecycle()
        : this(null)
    {
    }

    public DefaultAppLifecycle(Action<int>? shutdownDelegate)
    {
        _shutdownDelegate = shutdownDelegate;
    }

    public void Pause()
    {
        if (_isPaused) return;
        _isPaused = true;
        Paused?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (!_isPaused) return;
        _isPaused = false;
        Resumed?.Invoke(this, EventArgs.Empty);
    }

    public void Shutdown(int exitCode = 0)
    {
        ShuttingDown?.Invoke(this, exitCode);

        if (_shutdownDelegate != null)
        {
            _shutdownDelegate(exitCode);
        }
        else
        {
            WpfApplication.Current.Shutdown(exitCode);
        }
    }
}
