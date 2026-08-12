using Microsoft.Extensions.Logging;

namespace SqlAgent.Tests;

/// <summary>A single captured log call, flattened to the parts these tests assert on.</summary>
internal sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Captures what a component actually logged, so a test can assert on evidence rather than on absence.
/// Two of the interop failures the shell tolerates leave no other trace at all: the component catches
/// them, keeps its visible state, and returns normally, so "the page still works" is equally true
/// whether the catch ran or the exception escaped into a renderer path that discards it. The log line
/// is the only observable difference, which makes it the only thing worth asserting.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<LogRecord> _records = [];

    public IReadOnlyList<LogRecord> Records
    {
        get { lock (_records) return _records.ToList(); }
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(this);

    public void Dispose() { }

    private void Add(LogRecord record)
    {
        lock (_records) _records.Add(record);
    }

    private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add(new LogRecord(logLevel, formatter(state, exception), exception));
    }
}
