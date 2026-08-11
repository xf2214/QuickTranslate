using Microsoft.Extensions.Logging;
using QuickTranslate.Infrastructure.Logging;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class DebugLoggingSwitchTests
{
    [Fact]
    public void ToggleDebug_On_CounterDebugLogsAppear()
    {
        var spyProvider = new SpyLoggerProvider();
        bool Filter(LogLevel level)
        {
            if (LoggingSwitch.IsDebugEnabled)
                return true;
            return level >= LogLevel.Information;
        }
        var filter = new FilteringLoggerProvider(spyProvider, Filter);

        LoggingSwitch.IsDebugEnabled = true;
        try
        {
            var logger = filter.CreateLogger("Test");
            logger.LogDebug("test debug message");
            logger.LogInformation("test info message");

            int debugCount = spyProvider.Entries.Count(e => e.Level == LogLevel.Debug);
            int infoCount = spyProvider.Entries.Count(e => e.Level == LogLevel.Information);

            Assert.True(debugCount >= 1, $"Expected >=1 Debug logs, got {debugCount}");
            Assert.True(infoCount >= 1, $"Expected >=1 Info logs, got {infoCount}");
        }
        finally
        {
            LoggingSwitch.IsDebugEnabled = false;
        }
    }

    [Fact]
    public void ToggleDebug_Off_DebugLogsFiltered()
    {
        var spyProvider = new SpyLoggerProvider();
        bool Filter(LogLevel level)
        {
            if (LoggingSwitch.IsDebugEnabled)
                return true;
            return level >= LogLevel.Information;
        }
        var filter = new FilteringLoggerProvider(spyProvider, Filter);

        LoggingSwitch.IsDebugEnabled = false;

        var logger = filter.CreateLogger("Test");
        logger.LogDebug("test debug message");
        logger.LogInformation("test info message");
        logger.LogWarning("test warn message");

        int debugCount = spyProvider.Entries.Count(e => e.Level == LogLevel.Debug);
        int infoCount = spyProvider.Entries.Count(e => e.Level == LogLevel.Information);
        int warnCount = spyProvider.Entries.Count(e => e.Level == LogLevel.Warning);

        Assert.Equal(0, debugCount);
        Assert.True(infoCount >= 1, $"Expected >=1 Info logs, got {infoCount}");
        Assert.True(warnCount >= 1, $"Expected >=1 Warn logs, got {warnCount}");
    }
}
