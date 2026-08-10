namespace QuickTranslate.Core.Abstractions;

public interface IAppLifecycle
{
    void Pause();
    void Resume();
    void Shutdown();
}
