using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace bleak.Data.Anonymizer.Tests;

/// <summary>One captured log call: its category, level, event, rendered message and structured properties.</summary>
internal sealed record LogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Properties,
    Exception? Exception)
{
    public object? this[string name] => Properties.FirstOrDefault(p => p.Key == name).Value;

    public bool Has(string name) => Properties.Any(p => p.Key == name);

    /// <summary>Everything a sink could render: the message, every property value and any exception text.</summary>
    public string AllText()
        => string.Join("\n", new[] { Message, Exception?.ToString() ?? "" }
            .Concat(Properties.Select(p => $"{p.Key}={p.Value}")));
}

/// <summary>An in-memory <see cref="ILoggerFactory"/> for tests; records every enabled log call.</summary>
internal sealed class CapturingLoggerFactory(LogLevel minLevel = LogLevel.Trace) : ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, minLevel, _entries);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class CapturingLogger(string category, LogLevel minLevel, ConcurrentQueue<LogEntry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var props = state as IReadOnlyList<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
            sink.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), props.ToList(), exception));
        }
    }
}
