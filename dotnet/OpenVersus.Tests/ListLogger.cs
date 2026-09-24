using Microsoft.Extensions.Logging;

namespace OpenVersus.Tests;

/// <summary>An ILogger that keeps every formatted line, for asserting on what was logged.</summary>
public sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
}
