using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Bootstrap;
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
