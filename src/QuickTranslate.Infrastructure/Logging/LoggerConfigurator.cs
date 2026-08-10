using System.Globalization;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace QuickTranslate.Infrastructure.Logging;

public static class LoggerConfigurator
{
    public static ILoggerFactory Configure(AppSettings settings, string logDirectory)
    {
        var logLevel = settings.DebugLogging ? LogEventLevel.Verbose : LogEventLevel.Information;

        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "QuickTranslate-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                restrictedToMinimumLevel: logLevel,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{ProcessId}/{ThreadId}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return new SerilogLoggerFactory(Log.Logger, true);
    }
}
