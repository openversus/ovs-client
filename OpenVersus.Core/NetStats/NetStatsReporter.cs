using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenVersus.Net;

namespace OpenVersus.NetStats;

/// <summary>
/// Sends a match's figures (<see cref="NetStatsReport"/>) to the server, which shows them on its live matches page:
/// POST /ovs/netstats {"matchId", "delay", "ping", "depth"} (and "final": true for the match's last), with the client's
/// identity as every request of <paramref name="http"/> carries it. Sent from a pool thread, so the sampler never
/// waits on the network; a report made while the one before is still on its way is dropped (the next is ten seconds
/// later anyway), but the final one, which is always sent: it is what stays on the match. The body is a few dozen
/// bytes: compressing it would add a frame header and save nothing, so it goes as it is.
/// </summary>
public sealed class NetStatsReporter(IHttpTransport http, Uri url, ILogger log)
{
    /// <summary>The server route the figures are posted to.</summary>
    public const string Path = "/ovs/netstats";

    /// <summary>How long a report may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private int _sending;
    private int _failures;

    /// <summary>The body of a report.</summary>
    public static string Body(NetStatsReport report) => string.Create(CultureInfo.InvariantCulture,
        $$"""{"matchId":"{{Escape(report.MatchId)}}","delay":{{report.InputDelay}},"ping":{{report.Ping}},"depth":{{report.MeanRollbackDepth:F2}}{{(report.Final ? ",\"final\":true" : "")}}}""");

    /// <summary>Queues <paramref name="report"/>; returns at once.</summary>
    public void Send(NetStatsReport report)
    {
        if (report.Final)
        {
            ThreadPool.QueueUserWorkItem(_ => Post(report));
            return;
        }

        if (Interlocked.Exchange(ref _sending, 1) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Post(report);
            }
            finally
            {
                Volatile.Write(ref _sending, 0);
            }
        });
    }

    /// <summary>Sends <paramref name="report"/> and waits for the answer; false when it did not land.</summary>
    public bool Post(NetStatsReport report)
    {
        var result = http.Post(url, "application/json", Encoding.UTF8.GetBytes(Body(report)), Timeout);
        // The server answers {"ok":true}, or {"ok":false,"reason":...} for a report it did not keep.
        string answer = result.Ok ? result.Text : "";
        if (result.Ok && !answer.Contains("\"ok\":false", StringComparison.Ordinal))
        {
            _failures = 0;
            return true;
        }

        // A server that is down, has no such route yet, or does not keep the report: say so once, then every thirtieth
        // time (five minutes).
        _failures++;
        if (_failures == 1 || _failures % 30 == 0)
        {
            string why = result.Ok ? $"the server did not keep it: {(answer.Length > 200 ? answer[..200] : answer)}" : result.Error ?? $"HTTP {result.Status}";
            log.Warn($"[NetStats] the live report did not land (count={_failures}): {why}");
        }

        return false;
    }

    // A match id is hex from the server; anything else that could end the JSON string is dropped.
    private static string Escape(string text) => new([.. text.Where(c => c is not ('"' or '\\') && !char.IsControl(c))]);
}

/// <summary>
/// A logger that passes on warnings and errors only: what the sampler says of itself when the player has not asked
/// for a NetStats log ([Features] NetStats off), so a fault is still seen and nothing else is written.
/// </summary>
public sealed class WarningsOnly(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
