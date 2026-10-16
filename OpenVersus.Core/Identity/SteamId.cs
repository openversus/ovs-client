using System.Runtime.InteropServices;
using OpenVersus.Native;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Identity;

/// <summary>
/// Where the Steam id comes from when the environment did not carry it (Proton does not inject
/// it): the Steam API in whichever module exports it, else, on native Windows only, the most
/// recent user in loginusers.vdf. This waits up to a minute for the API, so it runs off the game
/// thread.
/// </summary>
public static unsafe class SteamId
{
    private static readonly string[] s_moduleNames = ["steam_api64.dll", "steamclient64.dll", "steamclient.dll", "steam_api.dll", "gameoverlayrenderer64.dll"];

    /// <summary>
    /// The user accessor under each name Steamworks has exported it by. The flat API versions it
    /// (the game's Steamworks 1.53 has only SteamAPI_SteamUser_v021), so the bare name alone
    /// never matched and the API route was always skipped.
    /// </summary>
    internal static readonly string[] UserAccessorNames =
        ["SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v020", "SteamAPI_SteamUser"];

    /// <summary>
    /// The Steam id as decimal text, or empty when there is none. Only the running game's Steam
    /// API is trusted unless <paramref name="allowLoginUsers"/>: loginusers.vdf names whoever
    /// signed in to Steam there last, which on a shared or switched Steam Deck is someone else's
    /// account, and the server would then log this player into it. The C++ client made the same
    /// call on Proton, Wine and CrossOver.
    /// </summary>
    public static string Resolve(ILogger log, bool allowLoginUsers = true)
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
            nint accessor = 0;
            foreach (string name in UserAccessorNames)
            {
                accessor = Kernel32.GetProcAddress(module, name);
                if (accessor != 0)
                {
                    break;
                }
            }

            var steamUser = (delegate* unmanaged[Cdecl]<nint>)accessor;
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
                        log.Info("[OVS] Steam id from the game's Steam API");
                        log.Debug($"[OVS] SteamID from API: {id}");
                        return id.ToString();
                    }
                }
            }
            else
            {
                log.Warn("[OVS] The game's Steam API has no user accessor this client knows");
            }
        }

        if (!allowLoginUsers)
        {
            log.Warn("[OVS] No Steam id from the game's Steam API; registering without one (loginusers.vdf is not trusted on this runtime)");
            return "";
        }

        return FromLoginUsers(log);
    }

    /// <summary>
    /// A session ticket from the game's Steam API (ISteamUser::GetAuthSessionTicket) as hex, or "" when there is no Steam
    /// API, no user or no ticket. The server checks Steam's signature on it (the app ownership ticket inside) and only
    /// then believes the Steam id. Steamworks 1.57+ (SteamUser_v023) takes a fourth argument, the remote identity; the
    /// game's 1.53 (v021) takes three. The ticket stays valid while the game runs; it is never logged.
    /// </summary>
    public static string SessionTicket(ILogger log) => SessionTicket(log, TimeSpan.Zero);

    /// <summary>
    /// How long a Steam launch's first registration waits for the game to load its Steam API
    /// (<see cref="IdentityRegistration.Run"/>). The client starts within the first second of the game, when the engine
    /// has not loaded steam_api64.dll yet; asked then, there was no module, no ticket, and the server never took the
    /// Steam id as proven (2026-10-16: every Steam launch on Windows, so a reinstall's new install id made a new account).
    /// The game's own login needs Steam too, so waiting for it does not hold the registration past the login.
    /// </summary>
    public static readonly TimeSpan ModuleWait = TimeSpan.FromSeconds(30);

    /// <summary><see cref="SessionTicket(ILogger)"/>, waiting up to <paramref name="moduleWait"/> for the Steam API module.</summary>
    public static string SessionTicket(ILogger log, TimeSpan moduleWait)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        nint module = FindSteamModule();
        while (module == 0 && waited.Elapsed < moduleWait)
        {
            Thread.Sleep(250);
            module = FindSteamModule();
        }

        if (module == 0)
        {
            log.Warn($"[OVS] session ticket: no Steam API module after {waited.Elapsed.TotalSeconds:F0} s");
            return "";
        }

        if (waited.ElapsedMilliseconds >= 250)
        {
            log.Info($"[OVS] session ticket: the game loaded its Steam API after {waited.ElapsedMilliseconds} ms");
        }

        string? version = null;
        nint accessor = 0;
        foreach (string name in UserAccessorNames)
        {
            accessor = Kernel32.GetProcAddress(module, name);
            if (accessor != 0)
            {
                version = name;
                break;
            }
        }

        nint function = Kernel32.GetProcAddress(module, "SteamAPI_ISteamUser_GetAuthSessionTicket");
        if (accessor == 0 || function == 0)
        {
            log.Warn("[OVS] session ticket: the Steam API has no user accessor or GetAuthSessionTicket");
            return "";
        }

        var steamUser = (delegate* unmanaged[Cdecl]<nint>)accessor;
        nint user = 0;
        for (int i = 0; i < 60 && user == 0; i++)
        {
            user = steamUser();
            if (user == 0)
            {
                Thread.Sleep(500);
            }
        }

        if (user == 0)
        {
            log.Warn("[OVS] session ticket: no Steam user");
            return "";
        }

        byte[] buffer = new byte[2048];
        uint length = 0;
        uint handle;
        fixed (byte* p = buffer)
        {
            handle = version == "SteamAPI_SteamUser_v023"
                ? ((delegate* unmanaged[Cdecl]<nint, byte*, int, uint*, nint, uint>)function)(user, p, buffer.Length, &length, 0)
                : ((delegate* unmanaged[Cdecl]<nint, byte*, int, uint*, uint>)function)(user, p, buffer.Length, &length);
        }

        log.Info($"[OVS] session ticket: handle {handle}, {length} bytes, via {version}");
        return length == 0 || length > buffer.Length ? "" : Convert.ToHexString(buffer, 0, (int)length);
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
