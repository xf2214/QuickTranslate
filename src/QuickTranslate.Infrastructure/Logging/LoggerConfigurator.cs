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

        // Serilog 内部错误（如文件被占用无法写入）默认静默吞掉，
        // 导致应用看似正常但日志完全丢失、故障无从排查；开启 SelfLog 落盘暴露。
        Serilog.Debugging.SelfLog.Enable(msg =>
        {
            try
            {
                var selfLogPath = Path.Combine(Path.GetTempPath(), "QuickTranslate-serilog-selflog.txt");
                File.AppendAllText(selfLogPath, $"[{DateTime.Now:O}] {msg}{Environment.NewLine}");
            }
            catch
            {
                // SelfLog 自身失败不能反过来影响应用
            }
        });

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
                shared: true,
                restrictedToMinimumLevel: logLevel,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{ProcessId}/{ThreadId}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return new SerilogLoggerFactory(Log.Logger, true);
    }
}
