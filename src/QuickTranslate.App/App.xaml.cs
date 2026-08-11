using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Bootstrap;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.SingleInstance;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;
using ShutdownMode = System.Windows.ShutdownMode;
using WpfApplication = System.Windows.Application;

namespace QuickTranslate.App;

public partial class App : WpfApplication
{
    private SingleInstanceGuard? _instanceGuard;
    private IHost? _host;
    private ILogger<App>? _logger;
    private IAppLifecycle? _appLifecycle;
    private ITrayIconService? _trayIconService;
    private IHotkeyBroker? _hotkeyBroker;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.TryEnsureSingle())
        {
            try
            {
                using (var tempLoggerFactory = LoggerFactory.Create(b => { }))
                {
                    var tempLogger = tempLoggerFactory.CreateLogger<App>();
                    tempLogger.LogWarning("Another instance is already running. Exiting.");
                }
            }
            catch { }
            _instanceGuard.Dispose();
            Environment.Exit(1);
            return;
        }

        try
        {
            _host = AppHost.Build();
            _host.Start();

            var sp = _host.Services;
            var loggerFactory = sp.GetRequiredService<global::Microsoft.Extensions.Logging.ILoggerFactory>();
            _logger = loggerFactory.CreateLogger<App>();

            RegisterGlobalExceptionHandlers(_logger, sp);

            _appLifecycle = sp.GetRequiredService<IAppLifecycle>();
            _appLifecycle.ShuttingDown += OnAppLifecycleShuttingDown;

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _logger.LogInformation("App started, Tray ready");

            _trayIconService = sp.GetRequiredService<ITrayIconService>();
            _trayIconService.Show();
            _logger.LogInformation("Tray icon shown");

            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            _hotkeyBroker = sp.GetRequiredService<IHotkeyBroker>();
            _hotkeyBroker.RegisterDefaultsFromSettings(settings);
            _logger.LogInformation("Hotkeys registered: Alt+D1 (Word), Alt+D2 (Block), Esc (Cancel)");
        }
        catch (Exception ex)
        {
            try
            {
                var crashDir = Environment.GetEnvironmentVariable("QUICKTRANSLATE_LOGDIR");
                if (string.IsNullOrEmpty(crashDir)) crashDir = Path.GetTempPath();
                Directory.CreateDirectory(crashDir);
                var crashFile = Path.Combine(crashDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(crashFile, $"[{DateTime.Now:O}] Startup FAILED:{Environment.NewLine}{ex}");
            }
            catch { }
            try
            {
                using (var tempLoggerFactory = LoggerFactory.Create(b => { }))
                {
                    var tempLogger = tempLoggerFactory.CreateLogger<App>();
                    tempLogger.LogCritical(ex, "Failed to start application");
                }
            }
            catch { }
            Cleanup();
            Environment.Exit(1);
        }
    }

    private void OnAppLifecycleShuttingDown(object? sender, int exitCode)
    {
        try
        {
            _logger?.LogInformation("App lifecycle shutting down with code {ExitCode}", exitCode);
        }
        catch { }

        try
        {
            _hotkeyBroker?.UnregisterAll();
        }
        catch { }

        CleanupTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _logger?.LogInformation("App exiting with code {ExitCode}", e.ApplicationExitCode);
        }
        catch { }

        Cleanup();

        try
        {
            global::Serilog.Log.CloseAndFlush();
        }
        catch { }

        base.OnExit(e);
    }

    private void CleanupTray()
    {
        try
        {
            if (_trayIconService != null)
            {
                _trayIconService.Hide();
                if (_trayIconService is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                _trayIconService = null;
            }
        }
        catch { }
    }

    private void RegisterGlobalExceptionHandlers(ILogger<App> logger, IServiceProvider sp)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = (Exception)e.ExceptionObject;
            logger.LogCritical(ex, "AppDomain Unhandled Exception (isTerminating={IsTerminating})", e.IsTerminating);
            TryCleanupAll(sp, logger);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            logger.LogError(e.Exception, "Dispatcher Unhandled Exception");
            TryCleanupAll(sp, logger);
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            logger.LogError(e.Exception, "Unobserved Task Exception");
            e.SetObserved();
        };
    }

    private static void TryCleanupAll(IServiceProvider sp, ILogger<App> logger)
    {
        try
        {
            var overlay = sp.GetService<ISelectionOverlayService>();
            overlay?.HideAll();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TryCleanupAll: Hide overlay failed");
        }

        try
        {
            var wordPopup = sp.GetService<IWordPopupService>();
            wordPopup?.HideAll();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TryCleanupAll: Hide word popup failed");
        }

        try
        {
            var blockPopup = sp.GetService<IBlockPopupService>();
            blockPopup?.HideAll();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TryCleanupAll: Hide block popup failed");
        }

        try
        {
            var wordCoord = sp.GetService<WordInteractionCoordinator>();
            wordCoord?.CancelAll(returnIdle: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TryCleanupAll: Cancel word coordinator failed");
        }

        try
        {
            var blockCoord = sp.GetService<BlockInteractionCoordinator>();
            blockCoord?.TryCancelAndClear(returnIdle: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TryCleanupAll: Cancel block coordinator failed");
        }
    }

    private void Cleanup()
    {
        if (_appLifecycle != null)
        {
            try
            {
                _appLifecycle.ShuttingDown -= OnAppLifecycleShuttingDown;
            }
            catch { }
            _appLifecycle = null;
        }

        CleanupTray();

        try
        {
            if (_host != null)
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
                _host = null;
            }
        }
        catch { }

        try
        {
            _instanceGuard?.Dispose();
            _instanceGuard = null;
        }
        catch { }
    }
}
