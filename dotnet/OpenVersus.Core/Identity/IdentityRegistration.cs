using System.Text;
using System.Text.Json;
using OpenVersus.Net;

namespace OpenVersus.Identity;

/// <summary>POSTs the player's identity to /api/identify. Runs on its own thread so launch never waits for it.</summary>
public static class IdentityRegistration
{
    public static string Body(EnvInfo env) =>
        JsonSerializer.Serialize(new IdentityBody(env.SteamId, env.EpicId, env.HardwareId, OvsVersion.Current), OvsJson.Default.IdentityBody);

    public static void Run(EnvInfo env, string serverUrl, IHttpTransport http, Log log)
    {
        if (string.IsNullOrEmpty(serverUrl))
        {
            return;
        }

        if (env.SteamId is "Unknown" or "")
        {
            string id = SteamId.Resolve(log);
            if (id.Length > 0)
            {
                env.SteamId = id;
                env.IsSteam = true;
            }
        }
        var url = Urls.Join(serverUrl, "/api/identify");
        if (url == null)
        {
            log.Warn($"[OVS] RegisterIdentity: cannot parse server URL \"{serverUrl}\"");
            return;
        }
        string body = Body(env);
        log.Debug(env.Print());
        log.Debug($"[OVS] RegisterIdentity: {body}");
        var result = http.Post(url, "application/json", Encoding.UTF8.GetBytes(body), TimeSpan.FromSeconds(5));
        if (result.Ok)
        {
            log.Debug("[OVS] RegisterIdentity: done");
        }
        else
        {
            log.Warn($"[OVS] RegisterIdentity: failed ({result.Error ?? $"HTTP {result.Status}"})");
        }
    }
}
