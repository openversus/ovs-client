using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using OpenVersus.Native;
using OpenVersus.Net;

namespace OpenVersus.Identity;

/// <summary>
/// The game's Epic Online Services SDK (EOSSDK-Win64-Shipping.dll, one of the game's delay-load imports) and the
/// Epic account ID token it can hand out: a JWT Epic signs for the game's client id, naming the Epic account the
/// game is logged into. The game never asks the SDK for one (it binds EOS_Auth_Login and the auth token calls,
/// not EOS_Auth_CopyIdToken), so this does, on the game's own platform handle.
/// </summary>
/// <remarks>
/// The handle: the game calls EOS_Platform_Tick every frame with it, through the delay-load import slot the loader
/// filled with the SDK's export at the first call. Once that slot holds the export, it is pointed at
/// <see cref="Tick"/>, which notes the handle and calls the export. The SDK is not thread-safe, so every SDK call
/// here runs inside that tick, on the game thread; the thread that starts this only reads what the tick wrote.
/// What it found is logged (a Steam launch logs into EOS Connect only, measured 2026-10-08: no Epic account, no
/// token; an Epic Games Store launch logs into the account during startup) and the token, when there is one, goes
/// to the server with the identity (<see cref="IdentityRegistration.RegisterEpicToken"/>).
/// </remarks>
public static unsafe class EpicIdentity
{
    /// <summary>The SDK's module name, as the game's delay-load import names it.</summary>
    public const string ModuleName = "EOSSDK-Win64-Shipping.dll";

    private const int CopyIdTokenApiVersion = 1;
    private const int EosSuccess = 0;

    // The SDK's exports, bound by GetProcAddress (the game's slots are not borrowed).
    private static delegate* unmanaged<nint, void> s_tick;
    private static delegate* unmanaged<nint, nint> s_getAuth;
    private static delegate* unmanaged<nint, nint> s_getConnect;
    private static delegate* unmanaged<nint, int> s_authCount;
    private static delegate* unmanaged<nint, int> s_connectCount;
    private static delegate* unmanaged<nint, int, nint> s_authByIndex;
    private static delegate* unmanaged<nint, nint, int> s_authStatus;
    private static delegate* unmanaged<nint, CopyIdTokenOptions*, IdToken**, int> s_copyIdToken;
    private static delegate* unmanaged<IdToken*, void> s_releaseIdToken;
    private static delegate* unmanaged<nint, byte*, int*, int> s_accountIdToString;
    private static delegate* unmanaged<int, byte*> s_resultToString;
    private static delegate* unmanaged<delegate* unmanaged<LogMessage*, void>, int> s_setLogCallback;
    private static delegate* unmanaged<int, int, int> s_setLogLevel;

    private static nint* s_slot;
    private static nint s_export;
    private static nint s_platform;
    private static long s_ticks;
    // The last counts logged, per platform handle: the game can tick more than one platform (seen on Windows, 2026-10-10:
    // one with a product user, one without), and shared counts flipped on every tick and logged each time.
    private static readonly Dictionary<nint, (int Auth, int Connect)> s_lastCounts = [];
    private static long s_startedAt;
    private static volatile int s_state;
    private static Outcome? s_outcome;
    private static ILogger? s_log;

    /// <summary>How long the tick waits for an Epic account login before giving up (the game's own login is at ~40 s).</summary>
    public static TimeSpan LoginWait { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>What the game's SDK had when the probe finished.</summary>
    /// <param name="AuthAccounts">Epic accounts logged in (EOS_Auth), or -1 when the auth interface was missing.</param>
    /// <param name="ConnectUsers">Product users logged in (EOS_Connect), or -1 when the connect interface was missing.</param>
    /// <param name="AccountId">The first Epic account's id, or "".</param>
    /// <param name="LoginStatus">EOS_ELoginStatus of that account (0 not logged in, 1 using local profile, 2 logged in).</param>
    /// <param name="Token">The ID token, or null.</param>
    /// <param name="Error">Why there is no token, or null.</param>
    public sealed record Outcome(int AuthAccounts, int ConnectUsers, string AccountId, int LoginStatus, string? Token, string? Error);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyIdTokenOptions
    {
        public int ApiVersion;
        public nint AccountId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdToken
    {
        public int ApiVersion;
        public nint AccountId;
        public byte* JsonWebToken;
    }

    /// <summary>EOS_LogMessage: the SDK's own log line (category, text, EOS_ELogLevel).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LogMessage
    {
        public byte* Category;
        public byte* Message;
        public int Level;
    }

    private const int LogAllCategories = 0x7fffffff;
    private const int LogLevelWarning = 300;
    private const int LogLevelInfo = 400;

    /// <summary>
    /// Waits for the SDK to be in the process and the game to have called its tick, takes the game's tick slot, then
    /// waits for an Epic account login and logs what the ID token says next to <paramref name="claimedEpicId"/>
    /// (the id the client reads from the Epic launcher's files, or "Unknown"). Runs on its own thread; returns when
    /// the probe has an outcome or gave up.
    /// </summary>
    public static Outcome? Probe(GameImage image, string claimedEpicId, ILogger log)
    {
        s_log = log;
        nint module = WaitForModule(log);
        if (module == 0)
        {
            return null;
        }

        if (!Bind(module, log))
        {
            return null;
        }

        if (!TakeTickSlot(image, log))
        {
            return null;
        }

        var waited = Stopwatch.StartNew();
        while (Volatile.Read(ref s_platform) == 0 && waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(100);
        }

        nint platform = Volatile.Read(ref s_platform);
        if (platform == 0)
        {
            log.Warn("[Epic] the game did not tick its platform within 60 s of the slot swap");
            return null;
        }

        log.Info($"[Epic] platform handle 0x{platform:X} captured after {waited.ElapsedMilliseconds} ms");
        while (s_state == 0 && waited.Elapsed < LoginWait + TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(250);
        }

        var outcome = s_outcome;
        QuietSdkLog();
        if (outcome == null)
        {
            log.Warn("[Epic] the tick never reported an outcome");
            return null;
        }

        Report(outcome, claimedEpicId, log);
        return outcome;
    }

    /// <summary>Puts the game's tick slot back; safe to call whether or not it was taken.</summary>
    public static void Restore()
    {
        if (s_slot != null && *s_slot == (nint)(delegate* unmanaged<nint, void>)&Tick)
        {
            *s_slot = s_export;
        }
    }

    private static nint WaitForModule(ILogger log)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(120))
        {
            nint module = Kernel32.GetModuleHandle(ModuleName);
            if (module != 0 && Kernel32.GetProcAddress(module, "EOS_Platform_Tick") != 0)
            {
                log.Info($"[Epic] {ModuleName} is loaded (after {waited.ElapsedMilliseconds} ms)");
                return module;
            }

            Thread.Sleep(1000);
        }

        log.Info($"[Epic] {ModuleName} not loaded within 120 s; no Epic identity on this launch");
        return 0;
    }

    private static bool Bind(nint module, ILogger log)
    {
        var missing = new List<string>();
        nint Export(string name)
        {
            nint p = Kernel32.GetProcAddress(module, name);
            if (p == 0)
            {
                missing.Add(name);
            }

            return p;
        }

        s_export = Export("EOS_Platform_Tick");
        s_tick = (delegate* unmanaged<nint, void>)s_export;
        s_getAuth = (delegate* unmanaged<nint, nint>)Export("EOS_Platform_GetAuthInterface");
        s_getConnect = (delegate* unmanaged<nint, nint>)Export("EOS_Platform_GetConnectInterface");
        s_authCount = (delegate* unmanaged<nint, int>)Export("EOS_Auth_GetLoggedInAccountsCount");
        s_connectCount = (delegate* unmanaged<nint, int>)Export("EOS_Connect_GetLoggedInUsersCount");
        s_authByIndex = (delegate* unmanaged<nint, int, nint>)Export("EOS_Auth_GetLoggedInAccountByIndex");
        s_authStatus = (delegate* unmanaged<nint, nint, int>)Export("EOS_Auth_GetLoginStatus");
        s_copyIdToken = (delegate* unmanaged<nint, CopyIdTokenOptions*, IdToken**, int>)Export("EOS_Auth_CopyIdToken");
        s_releaseIdToken = (delegate* unmanaged<IdToken*, void>)Export("EOS_Auth_IdToken_Release");
        s_accountIdToString = (delegate* unmanaged<nint, byte*, int*, int>)Export("EOS_EpicAccountId_ToString");
        s_resultToString = (delegate* unmanaged<int, byte*>)Export("EOS_EResult_ToString");
        if (missing.Count > 0)
        {
            log.Warn($"[Epic] the SDK lacks {string.Join(", ", missing)}; no Epic identity on this launch");
            return false;
        }

        // The SDK's own log, which the shipped game drops: at Info while the login is awaited (it says why a login
        // failed), Warning afterwards. One global callback; the game's, if it set one, is replaced.
        s_setLogCallback = (delegate* unmanaged<delegate* unmanaged<LogMessage*, void>, int>)Kernel32.GetProcAddress(module, "EOS_Logging_SetCallback");
        s_setLogLevel = (delegate* unmanaged<int, int, int>)Kernel32.GetProcAddress(module, "EOS_Logging_SetLogLevel");
        if (s_setLogCallback != null && s_setLogLevel != null)
        {
            int set = s_setLogCallback(&LogMessageReceived);
            int level = s_setLogLevel(LogAllCategories, LogLevelInfo);
            log.Info($"[Epic] SDK log capture: callback {ResultName(set)}, level {ResultName(level)}");
        }

        return true;
    }

    [UnmanagedCallersOnly]
    private static void LogMessageReceived(LogMessage* message)
    {
        HookGuard.Run("EpicLog", (nint)message, static p =>
        {
            var m = (LogMessage*)p;
            string category = Marshal.PtrToStringUTF8((nint)m->Category) ?? "";
            string text = Marshal.PtrToStringUTF8((nint)m->Message) ?? "";
            if (m->Level <= LogLevelWarning)
            {
                s_log?.Warn($"[EOS:{category}] {text}");
            }
            else
            {
                s_log?.Info($"[EOS:{category}] {text}");
            }
        });
    }

    private static void QuietSdkLog()
    {
        if (s_setLogLevel != null)
        {
            s_setLogLevel(LogAllCategories, LogLevelWarning);
        }
    }

    /// <summary>Points the game's delay-load slot for EOS_Platform_Tick at <see cref="Tick"/> once the loader has resolved it.</summary>
    private static bool TakeTickSlot(GameImage image, ILogger log)
    {
        uint rva = PeImage.DelayImportSlot(image.Bytes, ModuleName, "EOS_Platform_Tick");
        if (rva == 0)
        {
            log.Warn("[Epic] the game has no delay-load import of EOS_Platform_Tick; no Epic identity on this launch");
            return false;
        }

        var slot = (nint*)image.Address(rva);
        var waited = Stopwatch.StartNew();
        while (*slot != s_export && waited.Elapsed < TimeSpan.FromSeconds(120))
        {
            Thread.Sleep(250);
        }

        if (*slot != s_export)
        {
            log.Warn($"[Epic] the tick slot (rva 0x{rva:X}) holds 0x{*slot:X}, not the export 0x{s_export:X}, after 120 s: the game has not ticked EOS; left alone");
            return false;
        }

        if (!Kernel32.VirtualProtect((nint)slot, 8, Kernel32.PAGE_READWRITE, out uint old))
        {
            log.Warn($"[Epic] cannot make the tick slot writable ({Marshal.GetLastPInvokeError()}); left alone");
            return false;
        }

        s_slot = slot;
        s_startedAt = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref *slot, (nint)(delegate* unmanaged<nint, void>)&Tick);
        Kernel32.VirtualProtect((nint)slot, 8, old, out _);
        log.Info($"[Epic] tick slot (rva 0x{rva:X}) taken after {waited.ElapsedMilliseconds} ms");
        return true;
    }

    [UnmanagedCallersOnly]
    private static void Tick(nint platform)
    {
        HookGuard.Run("EpicTick", platform, static p => OnTick(p));
        s_tick(platform);
    }

    private static void OnTick(nint platform)
    {
        if (s_platform == 0)
        {
            Volatile.Write(ref s_platform, platform);
        }

        // Every tick while waiting: an Epic launch logs into the account, logs the product user in with it and can
        // log the account out again within seconds (seen 2026-10-08), so the token is copied the moment it exists.
        ++s_ticks;
        if (s_state != 0)
        {
            return;
        }

        nint auth = s_getAuth(platform);
        nint connect = s_getConnect(platform);
        int accounts = auth == 0 ? -1 : s_authCount(auth);
        int users = connect == 0 ? -1 : s_connectCount(connect);
        if (!s_lastCounts.TryGetValue(platform, out var last) || last != (accounts, users))
        {
            s_lastCounts[platform] = (accounts, users);
            s_log?.Info($"[Epic] logged in: {accounts} Epic account(s), {users} product user(s) on platform 0x{platform:X} (tick {s_ticks}, {Stopwatch.GetElapsedTime(s_startedAt).TotalSeconds:F1} s)");
        }

        if (accounts > 0)
        {
            s_outcome = CopyToken(auth, accounts, users);
            s_state = 1;
        }
        else if (Stopwatch.GetElapsedTime(s_startedAt) > LoginWait)
        {
            s_outcome = new Outcome(accounts, users, "", 0, null, $"no Epic account login within {LoginWait.TotalSeconds:F0} s");
            s_state = 1;
        }
    }

    private static Outcome CopyToken(nint auth, int accounts, int users)
    {
        nint account = s_authByIndex(auth, 0);
        string id = AccountIdString(account);
        int status = s_authStatus(auth, account);
        var options = new CopyIdTokenOptions { ApiVersion = CopyIdTokenApiVersion, AccountId = account };
        IdToken* token = null;
        int result = s_copyIdToken(auth, &options, &token);
        if (result != EosSuccess || token == null)
        {
            return new Outcome(accounts, users, id, status, null, $"EOS_Auth_CopyIdToken: {ResultName(result)}");
        }

        string jwt = Marshal.PtrToStringUTF8((nint)token->JsonWebToken) ?? "";
        s_releaseIdToken(token);
        return new Outcome(accounts, users, id, status, jwt, jwt.Length == 0 ? "EOS_Auth_CopyIdToken returned an empty token" : null);
    }

    private static string AccountIdString(nint account)
    {
        byte* buffer = stackalloc byte[64];
        int length = 64;
        return s_accountIdToString(account, buffer, &length) == EosSuccess ? Marshal.PtrToStringUTF8((nint)buffer) ?? "" : "";
    }

    private static string ResultName(int result)
    {
        byte* name = s_resultToString(result);
        return name == null ? result.ToString() : $"{Marshal.PtrToStringUTF8((nint)name)} ({result})";
    }

    private static void Report(Outcome outcome, string claimedEpicId, ILogger log)
    {
        log.Info($"[Epic] probe: {outcome.AuthAccounts} Epic account(s), {outcome.ConnectUsers} product user(s); account {(outcome.AccountId.Length == 0 ? "none" : outcome.AccountId)} (login status {outcome.LoginStatus}); launcher file says {claimedEpicId}");
        if (outcome.Token == null)
        {
            log.Info($"[Epic] probe: no ID token: {outcome.Error}");
            return;
        }

        var decoded = Jwt.Decode(outcome.Token);
        if (decoded == null)
        {
            log.Warn($"[Epic] probe: the ID token is not a JWT ({outcome.Token.Length} characters)");
            return;
        }

        log.Info($"[Epic] probe: ID token header {Compact(decoded.Value.Header)}");
        log.Info($"[Epic] probe: ID token payload {Compact(decoded.Value.Payload)}");
        log.Debug($"[Epic] probe: ID token {outcome.Token}");
    }

    private static string Compact(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, OvsJson.Default.JsonElement);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
