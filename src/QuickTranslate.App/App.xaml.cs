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
            catch (Exception ex)
            {
                global::Serilog.Log.Warning(ex, "[App.OnStartup] Failed to log duplicate-instance warning [ErrorCode=APP_INSTANCE_LOG_FAIL]");
            }
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
            _logger.LogInformation("Hotkeys registered: {WordHotkey} (Word), {BlockHotkey} (Block), Esc (Cancel)",
                $"{settings.WordHotkey.Modifiers}+{settings.WordHotkey.Key}",
                $"{settings.BlockHotkey.Modifiers}+{settings.BlockHotkey.Key}");
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
            catch (Exception crashEx)
            {
                global::Serilog.Log.Debug(crashEx, "[App.OnStartup] Failed to write crash file [ErrorCode=APP_CRASH_FILE_FAIL]");
            }
            try
            {
                using (var tempLoggerFactory = LoggerFactory.Create(b => { }))
                {
                    var tempLogger = tempLoggerFactory.CreateLogger<App>();
                    tempLogger.LogCritical(ex, "Failed to start application");
                }
            }
            catch (Exception logEx)
            {
                global::Serilog.Log.Warning(logEx, "[App.OnStartup] Failed to log critical startup failure [ErrorCode=APP_STARTUP_LOG_FAIL]");
            }
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
        catch (Exception ex)
        {
            if (_logger != null) _logger.LogDebug(ex, "[App.Shutdown] Failed to log lifecycle shutting down [ErrorCode=APP_LIFECYCLE_LOG_FAIL]");
            else global::Serilog.Log.Debug(ex, "[App.Shutdown] Failed to log lifecycle shutting down [ErrorCode=APP_LIFECYCLE_LOG_FAIL]");
        }

        try
        {
            _hotkeyBroker?.UnregisterAll();
        }
        catch (Exception ex)
        {
            if (_logger != null) _logger.LogDebug(ex, "[App.Shutdown] Hotkey UnregisterAll failed during lifecycle shutdown [ErrorCode=APP_HOTKEY_UNREGISTER_FAIL]");
            else global::Serilog.Log.Debug(ex, "[App.Shutdown] Hotkey UnregisterAll failed during lifecycle shutdown [ErrorCode=APP_HOTKEY_UNREGISTER_FAIL]");
        }

        CleanupTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _logger?.LogInformation("App exiting with code {ExitCode}", e.ApplicationExitCode);
        }
        catch (Exception ex)
        {
            if (_logger != null) _logger.LogDebug(ex, "[App.OnExit] Failed to log exit code [ErrorCode=APP_EXIT_LOG_FAIL]");
            else global::Serilog.Log.Debug(ex, "[App.OnExit] Failed to log exit code [ErrorCode=APP_EXIT_LOG_FAIL]");
        }

        Cleanup();

        try
        {
            global::Serilog.Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            // LAST: Serilog flush must remain last; failure is diagnostic only.
            try
            {
                _logger?.LogDebug(ex, "[App.OnExit] Serilog CloseAndFlush failed [ErrorCode=APP_SERILOG_FLUSH_FAIL]");
            }
            catch (Exception) { /* logger itself failing — swallow to preserve exit (intentionally unlogged, no diagnostic channel left) */ }
            global::Serilog.Log.Debug(ex, "[App.OnExit] Serilog CloseAndFlush failed [ErrorCode=APP_SERILOG_FLUSH_FAIL]");
        }

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
        catch (Exception ex)
        {
            if (_logger != null) _logger.LogDebug(ex, "[App.CleanupTray] Tray hide/dispose failed [ErrorCode=APP_TRAY_CLEANUP_FAIL]");
            else global::Serilog.Log.Debug(ex, "[App.CleanupTray] Tray hide/dispose failed [ErrorCode=APP_TRAY_CLEANUP_FAIL]");
        }
    }

    private void RegisterGlobalExceptionHandlers(ILogger<App> logger, IServiceProvider sp)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = (Exception)e.ExceptionObject;
            var cid = Guid.NewGuid().ToString("N")[..8];
            logger.LogCritical(ex, "[App.GlobalHandler] AppDomain Unhandled Exception (isTerminating={IsTerminating}) [CorrelationId={CorrelationId}] [ErrorCode=APP_DOMAIN_UNHANDLED]", e.IsTerminating, cid);
            TryCleanupAll(sp, logger);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            var cid = Guid.NewGuid().ToString("N")[..8];
            logger.LogError(e.Exception, "[App.GlobalHandler] Dispatcher Unhandled Exception [CorrelationId={CorrelationId}] [ErrorCode=APP_DISPATCHER_UNHANDLED] Handled=true — tray resilience swallow-by-design", cid);
            TryCleanupAll(sp, logger);
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            var cid = Guid.NewGuid().ToString("N")[..8];
            logger.LogError(e.Exception, "[App.GlobalHandler] Unobserved Task Exception [CorrelationId={CorrelationId}] [ErrorCode=APP_UNOBSERVED_TASK] SetObserved — tray resilience swallow-by-design", cid);
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
            catch (Exception ex)
            {
                if (_logger != null) _logger.LogDebug(ex, "[App.Cleanup] Unsubscribe ShuttingDown failed [ErrorCode=APP_UNSUBSCRIBE_FAIL]");
                else global::Serilog.Log.Debug(ex, "[App.Cleanup] Unsubscribe ShuttingDown failed [ErrorCode=APP_UNSUBSCRIBE_FAIL]");
            }
            _appLifecycle = null;
        }

        CleanupTray();

        // Bounded shutdown: avoid blocking UI thread via .GetAwaiter().GetResult() which can deadlock
        // when StopAsync continuations need the Dispatcher. Instead initiate StopAsync on a worker
        // and wait with timeout; tray already disposed above, Serilog CloseAndFlush stays LAST in OnExit after this returns.
        try
        {
            if (_host != null)
            {
                var hostToStop = _host;
                _host = null;
                try
                {
                    var stopTask = Task.Run(() => hostToStop.StopAsync(TimeSpan.FromSeconds(5)));
                    if (!stopTask.Wait(TimeSpan.FromSeconds(6)))
                    {
                        if (_logger != null) _logger.LogWarning("[App.Cleanup] Host StopAsync timed out after 6s [ErrorCode=APP_HOST_STOP_TIMEOUT]");
                        else global::Serilog.Log.Warning("[App.Cleanup] Host StopAsync timed out after 6s [ErrorCode=APP_HOST_STOP_TIMEOUT]");
                    }
                    else if (stopTask.IsFaulted && stopTask.Exception != null)
                    {
                        var ex = stopTask.Exception.InnerException ?? stopTask.Exception;
                        if (_logger != null) _logger.LogWarning(ex, "[App.Cleanup] Host StopAsync faulted [ErrorCode=APP_HOST_STOP_FAIL]");
                        else global::Serilog.Log.Warning(ex, "[App.Cleanup] Host StopAsync faulted [ErrorCode=APP_HOST_STOP_FAIL]");
                    }
                }
                finally
                {
                    try { hostToStop.Dispose(); }
                    catch (Exception ex)
                    {
                        if (_logger != null) _logger.LogDebug(ex, "[App.Cleanup] Host Dispose failed [ErrorCode=APP_HOST_DISPOSE_FAIL]");
                        else global::Serilog.Log.Debug(ex, "[App.Cleanup] Host Dispose failed [ErrorCode=APP_HOST_DISPOSE_FAIL]");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (_logger != null) _logger.LogWarning(ex, "[App.Cleanup] Host shutdown failed [ErrorCode=APP_HOST_SHUTDOWN_FAIL]");
            else global::Serilog.Log.Warning(ex, "[App.Cleanup] Host shutdown failed [ErrorCode=APP_HOST_SHUTDOWN_FAIL]");
        }

        try
        {
            _instanceGuard?.Dispose();
            _instanceGuard = null;
        }
        catch (Exception ex)
        {
            if (_logger != null) _logger.LogDebug(ex, "[App.Cleanup] SingleInstanceGuard dispose failed [ErrorCode=APP_GUARD_DISPOSE_FAIL]");
            else global::Serilog.Log.Debug(ex, "[App.Cleanup] SingleInstanceGuard dispose failed [ErrorCode=APP_GUARD_DISPOSE_FAIL]");
        }
    }
}
