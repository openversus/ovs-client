using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// logs/MatchRules.log, for testing the match rules (<see cref="StockRulesHooks"/>,
/// <see cref="FriendlyFireHooks"/>): each match's settings, deaths, and every hit friendly fire
/// looked at. Only with <c>[Settings.Debug] MatchRulesLog</c>; without it nothing is written or
/// even formatted beyond the string the caller builds, so callers check <see cref="On"/> before
/// building an expensive line.
/// </summary>
public static class MatchRulesLog
{
    private static ILogger? s_log;

    /// <summary>Whether the file is being written.</summary>
    public static bool On => s_log != null;

    /// <summary>Starts writing to <paramref name="log"/>.</summary>
    public static void Attach(ILogger log) => s_log = log;

    /// <summary>One line, when <see cref="On"/>.</summary>
    public static void Line(string text) => s_log?.Info(text);
}
