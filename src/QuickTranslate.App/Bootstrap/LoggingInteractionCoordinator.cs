using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;

namespace QuickTranslate.App.Bootstrap;

public class LoggingInteractionCoordinator : IInteractionCoordinator
{
    private readonly ILogger<LoggingInteractionCoordinator> _logger;
    private readonly IHotkeyBroker _hotkeyBroker;

    public LoggingInteractionCoordinator(
        ILogger<LoggingInteractionCoordinator> logger,
        IHotkeyBroker hotkeyBroker)
    {
        _logger = logger;
        _hotkeyBroker = hotkeyBroker;
        _hotkeyBroker.HotkeyFired += OnHotkeyFired;
    }

    private void OnHotkeyFired(object? sender, HotkeyEvent e)
    {
        HandleHotkey(e);
    }

    public void HandleHotkey(HotkeyEvent hotkeyEvent)
    {
        _logger.LogInformation("Hotkey fired: {Type} at {Timestamp:HH:mm:ss.fff}",
            hotkeyEvent.Type, hotkeyEvent.Timestamp);
    }

    public void ShowTranslationPopup(string sourceText, string translatedText)
    {
    }

    public void ShowSettingsWindow()
    {
    }
}
