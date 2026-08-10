using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickTranslate.App.Coordination;
using QuickTranslate.App.Services;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Infrastructure;
using QuickTranslate.Platform;
using QuickTranslate.Platform.Hotkeys;

namespace QuickTranslate.App.Bootstrap;

public static class AppHost
{
    public static IHost Build()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddInfrastructure(context.Configuration);
                services.AddPlatform();
                services.AddSingleton<ITrayIconService, WinFormsTrayIconService>();
                services.AddSingleton<IHotkeyBroker, DefaultHotkeyBroker>();
                services.AddSingleton<WordInteractionCoordinator>();
                services.AddSingleton<IInteractionCoordinator>(sp => sp.GetRequiredService<WordInteractionCoordinator>());
                services.AddSingleton<ISelectionOverlayService, WpfSelectionOverlayService>();
            })
            .ConfigureLogging((context, logging) =>
            {
            })
            .Build();

        return host;
    }
}
