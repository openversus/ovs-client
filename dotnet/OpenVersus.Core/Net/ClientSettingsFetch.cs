using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Net;

/// <summary>
/// Reads what the OpenVersus server sets for its clients, GET /ovs/client-settings, once at startup on a thread of its
/// own: <c>{"betaSpeedPercent": 110}</c>, the speed of a match with the Beta Speed mutator (the server's
/// BetaSpeed:Percent), handed to <paramref name="setBetaSpeed"/> (<see cref="Hooks.GameSpeedHooks.SetBetaSpeed"/>).
/// Every player's client and the rollback server must run the same speed, so it is the server's, never a setting here.
/// Tried three times, then given up: the client keeps 110, and the server turns it away from Testing Grounds when its
/// speed is another (X-OVS-BetaSpeed). <paramref name="wait"/> is the pause between tries (Thread.Sleep; the tests'
/// own).
/// </summary>
public sealed class ClientSettingsFetch(string serverUrl, IHttpTransport http, ILogger log, Func<int, bool> setBetaSpeed, Action<TimeSpan>? wait = null)
{
    private static readonly TimeSpan[] s_retryAfter = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];

    /// <summary>Asks until the server answers, three times at most. True when the server's Beta Speed was taken.</summary>
    public bool Run()
    {
        if (Urls.Join(serverUrl, "/ovs/client-settings") is not { } url)
        {
            log.Warn($"[ClientSettings] Failed to parse server URL: {serverUrl}; Beta Speed stays at {Hooks.GameSpeedHooks.DefaultBetaSpeedPercent}%");
            return false;
        }

        for (int attempt = 1; ; attempt++)
        {
            var result = http.Get(url, TimeSpan.FromSeconds(5));
            string problem;
            if (!result.Ok)
            {
                problem = result.Error ?? $"HTTP {result.Status}";
            }
            else if (ParseBetaSpeed(result.Text) is not { } percent)
            {
                problem = $"no betaSpeedPercent in the answer: {(result.Text.Length > 200 ? result.Text[..200] + "..." : result.Text)}";
            }
            else if (!setBetaSpeed(percent))
            {
                problem = $"betaSpeedPercent {percent} is not 50 to 200";
            }
            else
            {
                log.Info($"[ClientSettings] Beta Speed matches run at {percent}% (the server's)");
                return true;
            }

            if (attempt > s_retryAfter.Length)
            {
                log.Warn($"[ClientSettings] Gave up on {url} after {attempt} tries ({problem}); Beta Speed matches run at {Hooks.GameSpeedHooks.DefaultBetaSpeedPercent}%, and Testing Grounds refuses this game if the server's is another");
                return false;
            }

            TimeSpan pause = s_retryAfter[attempt - 1];
            log.Warn($"[ClientSettings] {url} failed (try {attempt}): {problem}; trying again in {pause.TotalSeconds:0} s");
            (wait ?? Thread.Sleep)(pause);
        }
    }

    /// <summary>The Beta Speed in a /ovs/client-settings answer; null when it is not an object with a whole betaSpeedPercent.</summary>
    public static int? ParseBetaSpeed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("betaSpeedPercent", out var percent)
                && percent.ValueKind == JsonValueKind.Number && percent.TryGetInt32(out int value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
