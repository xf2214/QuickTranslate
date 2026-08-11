using QuickTranslate.Core.Common;
using QuickTranslate.Core.Options;

namespace QuickTranslate.Core.Abstractions;

public interface IHotkeyBroker
{
    void RegisterDefaultsFromSettings(AppSettings settings);
    Result TryUpdateHotkeys(HotkeyCombo newWord, HotkeyCombo newBlock);
    bool Probe(HotkeyModifiers mods, KeyboardKey key);
    event EventHandler<HotkeyEvent>? HotkeyFired;
    void UnregisterAll();
}
