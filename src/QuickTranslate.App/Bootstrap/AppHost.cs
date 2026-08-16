using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.App.Services;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure;
using QuickTranslate.Infrastructure.Services;
using QuickTranslate.Platform;
using QuickTranslate.Platform.Hotkeys;
using QuickTranslate.TextToSpeech;

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
                services.AddSingleton<ITrayIconService>(sp => new WinFormsTrayIconService(
                    sp.GetRequiredService<IAppLifecycle>(),
                    sp.GetRequiredService<ILogger<WinFormsTrayIconService>>(),
                    sp));
                services.AddSingleton<IHotkeyBroker, DefaultHotkeyBroker>();
                services.AddSingleton<WordInteractionCoordinator>();
                services.AddSingleton<IInteractionCoordinator>(sp => sp.GetRequiredService<WordInteractionCoordinator>());
                services.AddSingleton<BlockInteractionCoordinator>();
                services.AddSingleton<ISelectionOverlayService, WpfSelectionOverlayService>();
                services.AddSingleton<IWordPopupService, WpfWordPopupService>();
                services.AddSingleton<IBlockPopupService, WpfBlockPopupService>();
                services.AddSingleton<IStatusIndicatorService, WpfStatusIndicatorService>();
                services.AddTextToSpeech();
            })
            .ConfigureLogging((context, logging) =>
            {
            })
            .Build();

        RunModelVersionVerification(host.Services);
        WireBlockHotkey(host.Services);
        WireWordHotkey(host.Services);

        return host;
    }

    private static void RunModelVersionVerification(IServiceProvider services)
    {
        try
        {
            var verifier = services.GetRequiredService<ModelVersionVerifier>();
            verifier.Verify();
        }
        catch (Exception ex)
        {
            using var tempLoggerFactory = LoggerFactory.Create(b => { });
            var tempLogger = tempLoggerFactory.CreateLogger(nameof(AppHost));
            tempLogger.LogWarning(ex, "ModelVersionVerifier.Verify failed on startup (non-fatal)");
        }
    }

    private static void WireWordHotkey(IServiceProvider services)
    {
        // 历史根因：WordInteractionCoordinator 只被 DI 注册、从未在生产路径实例化，
        // 其构造函数订阅 broker.HotkeyFired(Word) 的副作用因此从未生效，导致 Alt+1 热键
        // 触发后没有任何订阅者 → “按了没反应 / 保存成功但热键不生效”。
        // 这里显式解析一次以强制构造，使 Word 协调器完成 HotkeyFired(Word) 订阅。
        var wordCoord = services.GetRequiredService<WordInteractionCoordinator>();
        var logger = services.GetRequiredService<ILogger<WordInteractionCoordinator>>();
        logger.LogDebug("WordInteractionCoordinator wired: Alt+1 (Word) hotkey active");
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
