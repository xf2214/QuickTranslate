namespace QuickTranslate.Core.Abstractions;

public interface IStartupRegistrar
{
    bool IsEnabled { get; }
    void Enable(string? extraArgs = null);
    void Disable();
    string? GetCommandLine();
}
