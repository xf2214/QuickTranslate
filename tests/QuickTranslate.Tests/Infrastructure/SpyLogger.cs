using Microsoft.Extensions.Logging;

namespace QuickTranslate.Tests.Infrastructure;

public class SpyLogEntry
{
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public Exception? Exception { get; set; }
    public IReadOnlyList<KeyValuePair<string, object?>> State { get; set; } = Array.Empty<KeyValuePair<string, object?>>();
}

public class SpyLoggerProvider : ILoggerProvider
{
    private readonly List<SpyLogEntry> _entries = new();
    public IReadOnlyList<SpyLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName)
    {
        return new SpyLogger(categoryName, _entries);
    }

    public void Dispose() { }
}

public class SpyLogger<T> : ILogger<T>
{
    private readonly SpyLogger _inner;

    public SpyLogger(List<SpyLogEntry> entries)
    {
        _inner = new SpyLogger(typeof(T).FullName ?? typeof(T).Name, entries);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}

public class SpyLogger : ILogger
{
    private readonly string _categoryName;
    private readonly List<SpyLogEntry> _entries;

    public SpyLogger(string categoryName, List<SpyLogEntry> entries)
    {
        _categoryName = categoryName;
        _entries = entries;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var list = new List<KeyValuePair<string, object?>>();
        if (state is IEnumerable<KeyValuePair<string, object?>> enumerable)
        {
            list.AddRange(enumerable);
        }

        _entries.Add(new SpyLogEntry
        {
            Level = logLevel,
            Message = formatter(state, exception),
            Exception = exception,
            State = list
        });
    }
}
