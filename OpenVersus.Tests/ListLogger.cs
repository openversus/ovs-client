using Microsoft.Extensions.Logging;

namespace OpenVersus.Tests;

/// <summary>An ILogger that keeps every formatted line, for asserting on what was logged. Safe to
/// fill from one thread and read from another.</summary>
public sealed class ListLogger : ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add(formatter(state, exception));
        }
    }
}
