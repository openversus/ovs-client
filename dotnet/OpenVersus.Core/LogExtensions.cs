using Microsoft.Extensions.Logging;

namespace OpenVersus;

/// <summary>Event ids the client's own writer recognizes.</summary>
public static class LogEvents
{
    /// <summary>An Information line shown green on the console: the C++ printfSuccess.</summary>
    public static readonly EventId Success = new(1, "success");
}

/// <summary>
/// The client's short logging verbs on any <see cref="ILogger"/>. The message is passed as the
/// state and returned verbatim, never as a template, so text with braces in it (JSON bodies,
/// the game's own strings) is logged as it is.
/// </summary>
public static class LogExtensions
{
    public static void Trace(this ILogger log, string message) => Write(log, LogLevel.Trace, default, message);
    public static void Debug(this ILogger log, string message) => Write(log, LogLevel.Debug, default, message);
    public static void Info(this ILogger log, string message) => Write(log, LogLevel.Information, default, message);
    public static void Warn(this ILogger log, string message) => Write(log, LogLevel.Warning, default, message);
    public static void Error(this ILogger log, string message) => Write(log, LogLevel.Error, default, message);
    public static void Critical(this ILogger log, string message) => Write(log, LogLevel.Critical, default, message);
    public static void Success(this ILogger log, string message) => Write(log, LogLevel.Information, LogEvents.Success, message);

    private static void Write(ILogger log, LogLevel level, EventId eventId, string message) =>
        log.Log(level, eventId, message, null, static (state, _) => state);
}
