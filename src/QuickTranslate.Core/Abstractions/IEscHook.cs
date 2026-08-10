namespace QuickTranslate.Core.Abstractions;

public interface IEscHook
{
    event EventHandler? EscPressed;
    void RaiseEscPressed();
}
