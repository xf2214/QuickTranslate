using QuickTranslate.Core.Common;
using QuickTranslate.Core.Options;

namespace QuickTranslate.Core.Abstractions;

public interface IHotkeyBroker
{
    void RegisterDefaultsFromSettings(AppSettings settings);
    Result TryUpdateHotkeys(HotkeyCombo newWord, HotkeyCombo newBlock);
    event EventHandler<HotkeyEvent>? HotkeyFired;
    void UnregisterAll();
}
