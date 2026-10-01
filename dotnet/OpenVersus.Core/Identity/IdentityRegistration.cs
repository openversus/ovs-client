using System.Text;
using System.Text.Json;
using OpenVersus.Net;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Identity;

/// <summary>POSTs the player's identity to /api/identify. Runs on its own thread so launch never waits for it.</summary>
public static class IdentityRegistration
{
    /// <summary>
    /// The waits between attempts, as the C++ had them. The first request can race Windows
    /// networking at game start, and until one lands the game's /access finds no identity and
    /// the server can only go by the IP.
    /// </summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(2)];

    /// <summary>The JSON body: Steam id, Epic id, hardware id with its version and quality, install id and client version.</summary>
    public static string Body(EnvInfo env) =>
        JsonSerializer.Serialize(new IdentityBody(env.SteamId, env.EpicId, env.HardwareId, env.HardwareIdVersion, env.HardwareIdQuality, env.InstallId, OvsVersion.Current), OvsJson.Default.IdentityBody);

    /// <summary>What /api/identify sent back, or null when it is not that JSON.</summary>
    public static IdentifyResponse? ParseResponse(string json) => OvsJson.TryParse(json, OvsJson.Default.IdentifyResponse, out _);

    /// <summary>
    /// Registers this player, retrying while no answer arrives, so the server knows who is at this
    /// IP before the game logs in. When Steam launched the game and the environment lacked the Steam
    /// id, it is resolved first (which can take up to a minute) and sent in the one registration, as
    /// the C++ did: each /api/identify replaces what the server holds for the IP, so a first request
    /// without it could leave the game's login with no identity and a new account. Other launches
    /// (Epic, the Internet Archive build) have no Steam module to wait for, so they register at once
    /// and again if a Steam id turns up. The token each registration returns goes to
    /// <paramref name="identity"/>. Failures are logged. <paramref name="resolveSteamId"/> stands in
    /// for <see cref="SteamId.Resolve"/> in tests.
    /// </summary>
    public static void Run(EnvInfo env, string serverUrl, IHttpTransport http, ServerIdentity identity, ILogger log, Action<TimeSpan>? sleep = null, Func<string>? resolveSteamId = null)
    {
        if (string.IsNullOrEmpty(serverUrl))
        {
            return;
        }

        var url = Urls.Join(serverUrl, "/api/identify");
        if (url == null)
        {
            log.Warn($"[OVS] RegisterIdentity: cannot parse server URL \"{serverUrl}\"");
            return;
        }

        sleep ??= Thread.Sleep;
        resolveSteamId ??= () => SteamId.Resolve(log, allowLoginUsers: env.Runtime == RuntimeEnvironment.NativeWindows);
        bool steamIdMissing = env.SteamId is "Unknown" or "";
        if (steamIdMissing && env.IsSteamLaunch)
        {
            UseSteamId(env, resolveSteamId());
        }

        log.Debug(env.Print());
        bool registered = false;
        for (int attempt = 0; ; attempt++)
        {
            var outcome = Send(env, url, http, identity, log);
            if (outcome != Outcome.NoAnswer)
            {
                registered = outcome == Outcome.Registered;
                break;
            }

            if (attempt == RetryDelays.Length)
            {
                log.Error("[OVS] RegisterIdentity: no answer after retries; the server can only go by the IP this session");
                break;
            }

            log.Warn($"[OVS] RegisterIdentity: attempt {attempt + 1} got no answer; retrying in {RetryDelays[attempt].TotalMilliseconds} ms");
            sleep(RetryDelays[attempt]);
        }

        if (steamIdMissing && !env.IsSteamLaunch)
        {
            if (UseSteamId(env, resolveSteamId()))
            {
                Send(env, url, http, identity, log);
            }
            else if (!registered)
            {
                log.Warn("[OVS] RegisterIdentity: no Steam id either");
            }
        }
    }

    /// <summary>Takes a resolved Steam id into <paramref name="env"/>; false when there is none.</summary>
    private static bool UseSteamId(EnvInfo env, string id)
    {
        if (id.Length == 0)
        {
            return false;
        }

        env.SteamId = id;
        env.IsSteam = true;
        return true;
    }

    private enum Outcome
    {
        Registered,
        Refused,
        NoAnswer,
    }

    /// <summary>One POST. A server error counts as no answer, so it is retried; any other refusal is final.</summary>
    private static Outcome Send(EnvInfo env, Uri url, IHttpTransport http, ServerIdentity identity, ILogger log)
    {
        // The body is not logged: the install id stands in for an account for players without a
        // Steam or Epic id, and logs get shared.
        log.Debug($"[OVS] RegisterIdentity: sending (Steam {(env.IsSteam ? "yes" : "no")}, Epic {(env.IsEpic ? "yes" : "no")}, install id {(env.InstallId.Length > 0 ? "yes" : "no")}, hardware id {(env.HardwareId.Length > 0 ? "yes" : "no")})");
        var result = http.Post(url, "application/json", Encoding.UTF8.GetBytes(Body(env)), TimeSpan.FromSeconds(5));
        if (result.Ok)
        {
            string? token = ParseResponse(result.Text)?.Token;
            identity.Token = token ?? "";
            log.Debug($"[OVS] RegisterIdentity: done ({(identity.Token.Length > 0 ? "token kept" : "no token")})");
            return Outcome.Registered;
        }

        if (result.Status == 0 || result.Status >= 500)
        {
            log.Warn($"[OVS] RegisterIdentity: failed ({result.Error ?? $"HTTP {result.Status}"})");
            return Outcome.NoAnswer;
        }

        string? error = ParseResponse(result.Text)?.Error;
        log.Warn($"[OVS] RegisterIdentity: refused (HTTP {result.Status}{(string.IsNullOrEmpty(error) ? "" : $", {error}")})");
        return Outcome.Refused;
    }
}
