using System.Diagnostics;
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
    /// <summary>
    /// Build the Host. DI composition no longer blocks on disk/DPAPI IO:
    /// ServiceCollectionExtensions now eager-starts SettingsManager.LoadAsync at registration time
    /// and a HostedService (SettingsInitializationService) awaits it during Host.StartAsync.
    /// Build itself stays synchronous and fast (Tray Ready &lt;1s). The HostedService patches the
    /// cached IOptions.AppSettings value in-place before any hotkey can fire (hotkeys are only
    /// pumped after the WPF message loop starts, which is after host.Start completes).
    /// Wire*Hotkey / WarmUp chain is intentionally left AS-IS this wave; their IOptions reads
    /// during Build may transiently see defaults, but are corrected in-place before first use.
    /// Coordinators capture IOptions reference and read Value at pipeline time, SettingsWindow
    /// is opened lazily after Start, so both observe the loaded settings. This avoids
    /// sync-over-async deadlock/startup jank while preserving the 'settings ready before first hotkey' invariant.
    /// </summary>
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
        WarmUpInBackground(host.Services);

        return host;
    }

    /// <summary>
    /// 后台预热耗时组件：ONNX 会话（det/cls/rec）首次创建可达数百毫秒到秒级，
    /// 本地词典 packed 解析也要数十毫秒——若不预热，首次按热键的识别会吞下全部加载耗时。
    /// 预热失败不致命：真实管线首次识别时仍会惰性初始化。
    /// </summary>
    private static void WarmUpInBackground(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("QuickTranslate.App.Bootstrap.AppHost");
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var ocr = services.GetRequiredService<IOcrEngine>();
                await ocr.WarmUpAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OCR warm-up failed (non-fatal); first recognition will initialize lazily");
            }

            try
            {
                // 触发 Lazy 词典快照加载（packed 解析 + FrozenDictionary 构建）
                _ = services.GetRequiredService<ILocalDictionary>().Count;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dictionary warm-up failed (non-fatal)");
            }

            logger.LogInformation("Warm-up finished in {Ms}ms", sw.ElapsedMilliseconds);
        });
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
        var settings = services.GetRequiredService<IOptions<AppSettings>>().Value;
        var logger = services.GetRequiredService<ILogger<WordInteractionCoordinator>>();
        logger.LogDebug("WordInteractionCoordinator wired: {Hotkey} (Word) hotkey active",
            $"{settings.WordHotkey.Modifiers}+{settings.WordHotkey.Key}");
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
