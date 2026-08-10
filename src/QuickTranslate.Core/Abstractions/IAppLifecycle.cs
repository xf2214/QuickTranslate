namespace QuickTranslate.Core.Abstractions;

public interface IAppLifecycle
{
    bool IsPaused { get; }

    void Pause();

    void Resume();

    void Shutdown(int exitCode = 0);

    event EventHandler? Paused;

    event EventHandler? Resumed;

    event EventHandler<int>? ShuttingDown;
}
