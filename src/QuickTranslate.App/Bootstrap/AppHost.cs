using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.App.Services;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
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
                services.AddSingleton<BlockInteractionCoordinator>();
                services.AddSingleton<ISelectionOverlayService, WpfSelectionOverlayService>();
                services.AddSingleton<IWordPopupService, WpfWordPopupService>();
                services.AddSingleton<IBlockPopupService, WpfBlockPopupService>();
            })
            .ConfigureLogging((context, logging) =>
            {
            })
            .Build();

        WireBlockHotkey(host.Services);

        return host;
    }

    private static void WireBlockHotkey(IServiceProvider services)
    {
        var broker = services.GetRequiredService<IHotkeyBroker>();
        var blockCoord = services.GetRequiredService<BlockInteractionCoordinator>();
        var settings = services.GetRequiredService<IOptions<AppSettings>>().Value;
        var logger = services.GetRequiredService<ILogger<BlockInteractionCoordinator>>();

        blockCoord.Start();

        broker.HotkeyFired += (sender, e) =>
        {
            if (e.Type == HotkeyEventType.Block)
            {
                logger.LogDebug("Block hotkey triggered");
                blockCoord.RunBlockPipeline();
            }
        };
    }
}
