using System.Runtime.InteropServices;
using OpenVersus.Native;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Identity;

/// <summary>
/// Where the Steam id comes from when the environment did not carry it (Proton does not inject
/// it): the Steam API in whichever module exports it, else the most recent user in
/// loginusers.vdf. This waits up to a minute for the API, so it runs off the game thread.
/// </summary>
public static unsafe class SteamId
{
    private static readonly string[] s_moduleNames = ["steam_api64.dll", "steamclient64.dll", "steamclient.dll", "steam_api.dll", "gameoverlayrenderer64.dll"];

    public static string Resolve(ILogger log)
    {
        nint module = 0;
        for (int i = 0; i < 60 && module == 0; i++)
        {
            module = FindSteamModule();
            if (module == 0)
            {
                Thread.Sleep(500);
            }
        }
        if (module != 0)
        {
            var steamUser = (delegate* unmanaged[Cdecl]<nint>)Kernel32.GetProcAddress(module, "SteamAPI_SteamUser");
            var getSteamId = (delegate* unmanaged[Cdecl]<nint, ulong>)Kernel32.GetProcAddress(module, "SteamAPI_ISteamUser_GetSteamID");
            if (steamUser != null && getSteamId != null)
            {
                nint user = 0;
                for (int i = 0; i < 60 && user == 0; i++)
                {
                    user = steamUser();
                    if (user == 0)
                    {
                        Thread.Sleep(500);
                    }
                }
                if (user != 0)
                {
                    ulong id = getSteamId(user);
                    if (id != 0)
                    {
                        log.Debug($"[OVS] SteamID from API: {id}");
                        return id.ToString();
                    }
                }
            }
        }
        return FromLoginUsers(log);
    }

    private static nint FindSteamModule()
    {
        foreach (string name in s_moduleNames)
        {
            nint h = Kernel32.GetModuleHandle(name);
            if (h != 0 && Kernel32.GetProcAddress(h, "SteamAPI_ISteamUser_GetSteamID") != 0)
            {
                return h;
            }
        }
        foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
        {
            if (Kernel32.GetProcAddress(m.BaseAddress, "SteamAPI_ISteamUser_GetSteamID") != 0)
            {
                return m.BaseAddress;
            }
        }

        return 0;
    }

    /// <summary>The MostRecent user in loginusers.vdf, from the Proton paths, the home paths, then the Steam Deck defaults.</summary>
    private static string FromLoginUsers(ILogger log)
    {
        var candidates = new List<string>();
        string? compat = Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH");
        if (!string.IsNullOrEmpty(compat))
        {
            candidates.Add(compat + "/config/loginusers.vdf");
            candidates.Add(compat + "\\config\\loginusers.vdf");
        }
        string? home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(home + "/.steam/steam/config/loginusers.vdf");
            candidates.Add(home + "/.local/share/Steam/config/loginusers.vdf");
        }
        candidates.Add("/home/deck/.steam/steam/config/loginusers.vdf");
        candidates.Add("/home/deck/.local/share/Steam/config/loginusers.vdf");
        candidates.Add(@"Z:\home\deck\.steam\steam\config\loginusers.vdf");
        candidates.Add(@"Z:\home\deck\.local\share\Steam\config\loginusers.vdf");

        foreach (string path in candidates)
        {
            string[] lines;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                lines = File.ReadAllLines(path);
            }
            catch
            {
                continue;
            }
            string lastId = "", mostRecent = "";
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length > 2 && line[0] == '"' && line[^1] == '"')
                {
                    string key = line[1..^1];
                    if (key.Length is >= 15 and <= 20 && key.All(char.IsAsciiDigit))
                    {
                        lastId = key;
                    }
                }
                if (lastId.Length > 0 && line.Contains("\"MostRecent\"") && line.Contains("\"1\""))
                {
                    mostRecent = lastId;
                }
            }
            string result = mostRecent.Length > 0 ? mostRecent : lastId;
            if (result.Length > 0)
            {
                log.Debug($"[OVS] SteamID from file: {result}");
                return result;
            }
        }
        return "";
    }
}
