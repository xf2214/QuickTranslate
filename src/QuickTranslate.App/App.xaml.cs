using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickTranslate.App.Bootstrap;
using QuickTranslate.Infrastructure.SingleInstance;

namespace QuickTranslate.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private IHost? _host;
    private ILogger<App>? _logger;

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

            var loggerFactory = _host.Services.GetRequiredService<global::Microsoft.Extensions.Logging.ILoggerFactory>();
            _logger = loggerFactory.CreateLogger<App>();

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _logger.LogInformation("App started, Tray ready");
        }
        catch (Exception ex)
        {
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _logger?.LogInformation("App exiting with code {ExitCode}", e.ApplicationExitCode);
        }
        catch { }

        Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
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

        try
        {
            global::Serilog.Log.CloseAndFlush();
        }
        catch { }
    }
}

