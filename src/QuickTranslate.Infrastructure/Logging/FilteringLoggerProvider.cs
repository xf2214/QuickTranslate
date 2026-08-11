using Microsoft.Extensions.Logging;

namespace QuickTranslate.Infrastructure.Logging;

public class FilteringLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;
    private readonly Func<LogLevel, bool> _filter;

    public FilteringLoggerProvider(ILoggerProvider inner, Func<LogLevel, bool> filter)
    {
        _inner = inner;
        _filter = filter;
    }

    public ILogger CreateLogger(string categoryName)
    {
        var innerLogger = _inner.CreateLogger(categoryName);
        return new FilteringLogger(innerLogger, _filter);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}

public class FilteringLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly Func<LogLevel, bool> _filter;

    public FilteringLogger(ILogger inner, Func<LogLevel, bool> filter)
    {
        _inner = inner;
        _filter = filter;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return _filter(logLevel) && _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
