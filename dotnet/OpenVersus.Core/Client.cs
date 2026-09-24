using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenVersus.Config;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Hooks;
using OpenVersus.Identity;
using OpenVersus.Memory;
using OpenVersus.Native;
using OpenVersus.Net;
using OpenVersus.NetStats;

namespace OpenVersus;

/// <summary>
/// The client's startup, in the order the C++ OnInitializeHook ran it: settings, console,
/// process check, keyboard hook, image hash and pattern cache, then each patch and hook its
/// setting enables, then the background work.
/// </summary>
public sealed class Client
{
    private static readonly string[] s_gameProcessNames = ["MultiVersus-Win64-Shipping.exe", "MultiVersus.exe", "OVS.exe"];

    public Log Log { get; }
    public string Directory { get; }
    public Settings Settings { get; private set; } = null!;
    public State State { get; private set; } = null!;
    public HookStatus Status { get; } = new();
    public GameImage Image { get; private set; } = null!;
    public PatternResolver Patterns { get; private set; } = null!;
    public ObjectFinder Objects { get; private set; } = null!;
    public EnvInfo? Env { get; private set; }
    public IHttpTransport Http { get; set; } = new WinHttpTransport();

    private readonly string _pluginPath;

    private readonly nint _module;
    private readonly List<Action> _shutdown = [];

    public Client(Log log, string pluginPath, nint module)
    {
        Log = log;
        Directory = Path.GetDirectoryName(pluginPath)!;
        _pluginPath = pluginPath;
        _module = module;
    }

    public bool Initialize()
    {
        Log.Info($"On Attach Initialize ({OvsVersion.Name} {OvsVersion.Current})");
        Settings = Settings.Load(Path.Combine(Directory, Settings.FileName), Log);
        State = new State(Path.Combine(Directory, State.FileName)).Load();
        if (Log.Notice != null)
        {
            Log.Warn(Log.Notice);
        }
        else if (Log.FileError != null)
        {
            Log.Warn($"log file {Log.Path} cannot be written ({Log.FileError.Message}); logging to the console only");
        }

        LogLevel level = Log.ResolveLevel(Settings.LogLevel, Settings.Debug, out string? note);
        if (note != null)
        {
            Log.Warn(note);
        }

        Log.MinimumLevel = level;
        Log.Info($"log level {Log.MinimumLevel} (LogLevel=\"{Settings.LogLevel}\", DebugLogging={Settings.Debug})");
        Log.Debug($"[OVS] INI File: {Settings.Path}");

        string process = Path.GetFileName(Environment.ProcessPath ?? "");
        bool isGame = s_gameProcessNames.Any(n => string.Equals(n, process, StringComparison.OrdinalIgnoreCase));
        if (!Settings.AllowNonMvs && !isGame)
        {
            User32.MessageBox(0, "OVS only works with the original MVS steam game! Don't be surprised if it crashes.", OvsVersion.Name, User32.MB_ICONEXCLAMATION);
        }

        if (Settings.EnableConsoleWindow && !ConsoleWindow.Create(Log))
        {
            Log.Warn("Could not create the console window");
        }

        Log.Info($"host {Environment.ProcessPath}, pid {Environment.ProcessId}, {Environment.OSVersion}{(Wine.IsWine ? $", Wine {Wine.Version}" : "")}");

        if (Settings.EnableKeyboardHotkeys && KeyboardHook.Install(Log, _module))
        {
            _shutdown.Add(KeyboardHook.Remove);
        }

        if (Settings.PauseOnStart)
        {
            User32.MessageBox(0, "Freezing Game Until OK", ":)", User32.MB_ICONINFORMATION);
        }

        Image = GameImage.Host();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ulong hash = Image.HashTextSection();
        Log.Debug($".text hash: 0x{hash:X16} | Time: {stopwatch.Elapsed.TotalMilliseconds:F3} ms");
        var cache = new PatternCache(Path.Combine(Directory, PatternCache.FileName), hash, OvsVersion.Current);
        Patterns = new PatternResolver(Image, cache, Settings, Log);
        Log.Info("Parsed Settings");

        // Collect Steam/Epic identity and hardware fingerprint. It makes accounts "sticky", so
        // nobody resets their name and perks when their IP changes or they switch to Proton.
        Env = new EnvInfo();

        ApplyHooks();
        GameThread.Attach(Log);
        SpawnP2PServer();
        Objects = new ObjectFinder(Image, ProcessMemory.Instance, EngineNames.Instance, Log, tryObjectArray: Status.UeFuncs);
        StartBackgroundWork();
        return true;
    }

    /// <summary>Kept from the C++ client for the planned peer-to-peer rollback experiment.</summary>
    private void SpawnP2PServer()
    {
    }
    private void DespawnP2PServer()
    {
    }

    private void StartBackgroundWork()
    {
        // NativeAOT cannot run managed code at process detach any more than at attach, so
        // there is no shutdown moment to summarize in; a heartbeat reports instead, and the
        // game window going away is taken as the exit signal for closing the log.
        Start("OVS heartbeat", Heartbeat);
        Start("OVS exit watch", WatchForExit);

        if (Settings.EnableServerProxy && !string.IsNullOrEmpty(Settings.ServerUrl))
        {
            var poller = new NotificationPoller(Settings.ServerUrl, Http, Objects, Image, Log);
            poller.Start();
            _shutdown.Add(poller.Stop);
        }

        if (Status.UeFuncs && Status.Dialog)
        {
            var state = State;
            Start("OVS startup notices", () => StartupNotices.Run(state, Log));
        }

        var env = Env!;
        Start("OVS identity", () => IdentityRegistration.Run(env, Settings.ServerUrl, Http, Log));

        if (Settings.AutoUpdate)
        {
            Log.Info("[AutoUpdate] Auto-update is enabled. OVS will check for updates automatically and download/apply them when available.");
            var update = new AutoUpdate(Settings.ServerUrl, _pluginPath, Http, new WinHttpTransport(useSystemProxy: true), Log, Log.Close);
            Start("OVS auto-update", update.Run);
        }
        else
        {
            Log.Warn("[AutoUpdate] AutoUpdate is disabled in your config file. Don't be surprised if the game doesn't work correctly, or if it doesn't even work at all.");
            Log.Warn("[AutoUpdate] The latest version of OpenVersus can always be obtained from: https://github.com/openversus/ovs-client");
            Log.Warn("[AutoUpdate] If you want to enable auto-updates, set AutoUpdate=true in the [Settings] section of your config file.");
            Log.Warn("[AutoUpdate] Good luck, hopefully the game still works for you.");
        }

        if (Settings.NetStats)
        {
            // One file per match beside the main log, with the same writer thread, archive
            // and retention behavior; a line a second must not bury everything else. Each match
            // opens a fresh NetStats.log and closing it archives it under the match's start time.
            string logDirectory = Path.GetDirectoryName(Log.Path)!;
            string fallback = Directory;
            var stats = new NetStatsLogger(Objects, Log, info =>
            {
                var matchLog = Log.OpenSession(logDirectory, "NetStats", fallbackDirectory: fallback);
                matchLog.MinimumLevel = LogLevel.Information;
                matchLog.ArchiveSuffix = info.FileSuffix;
                if (matchLog.Notice != null)
                {
                    Log.Warn(matchLog.Notice);
                }

                return matchLog;
            });
            stats.Start();
            _shutdown.Add(stats.Stop);
        }
    }

    private void Start(string name, Action work)
    {
        var log = Log;
        new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e) { log.Error($"{name}: {e}"); }
        })
        {
            IsBackground = true,
            Name = name
        }.Start();
    }

    private void ApplyHooks()
    {
        var c = new HookContext(Image, Patterns, Settings, State, Log, Status);
        if (Settings.DisableSignatureCheck)
        {
            Status.AntiSigCheck = Apply("SigCheck", c, SigCheckPatch.Apply);
        }

        if (Settings.EnableServerProxy)
        {
            Status.GameEndpointSwap = Apply("EndpointLoader", c, EndpointHooks.ApplyGame);
        }

        if (Settings.EnableProdServerProxy)
        {
            Status.ProdEndpointSwap = Apply("ProdEndpointLoader", c, EndpointHooks.ApplyProd);
        }

        if (Settings.SunsetDate)
        {
            Status.SunsetDate = Apply("SunsetDate", c, ctx => SunsetPatch.Apply(ctx, count: Settings.CountSunsetCalls));
        }

        if (Settings.SunsetCallers)
        {
            Status.SunsetCallers = Apply("SunsetCallers", c, SunsetCallersPatch.Apply);
        }

        if (Settings.HookUe)
        {
            Status.UeFuncs = Apply("UE Funcs", c, UeFunctionHooks.Apply);
        }

        if (Settings.Dialog)
        {
            Status.Dialog = Apply("Dialog", c, DialogHooks.Apply);
        }

        if (Settings.Notifications)
        {
            Status.Notifications = Apply("Notifications", c, NotificationHooks.Apply);
        }

        if (Settings.PostMatchFreeze)
        {
            Status.PostMatchFreeze = Apply("PostMatchFreeze", c, PostMatchFreezePatch.Apply);
        }

        Log.Info($"hooks: {Status}");
        foreach (var f in GameFunctions.All)
        {
            Log.Debug($"function {f}");
        }
    }

    /// <summary>
    /// Runs one hook's Apply. A patch that cannot be made throws a <see cref="PatchException"/>
    /// out of the memory layer; any failure is logged as that hook's and the rest still apply.
    /// </summary>
    private bool Apply(string name, HookContext c, Func<HookContext, bool> apply)
    {
        try
        {
            return apply(c);
        }
        catch (Exception e)
        {
            Log.Error(e is PatchException ? $"{name}: {e.Message}" : $"{name}: {e}");
            return false;
        }
    }

    /// <summary>
    /// Once a minute: which hooks have failed and how often, when that changes; and when the
    /// sunset call counter is on, how many calls the last minute saw.
    /// </summary>
    private void Heartbeat()
    {
        string lastFailures = "";
        long lastCalls = 0;
        while (true)
        {
            Thread.Sleep(60_000);
            string failures = string.Join(", ", HookGuard.Failures.Select(kv => $"{kv.Key}={kv.Value}"));
            if (failures != lastFailures)
            {
                Log.Info($"heartbeat: hook failures: {(failures.Length == 0 ? "none" : failures)}");
            }

            lastFailures = failures;
            if (SunsetPatch.Counting)
            {
                long calls = SunsetPatch.Calls;
                Log.Info($"heartbeat: sunset check called {calls - lastCalls} times in the last minute ({calls} total){(Status.SunsetCallers ? ", with callers patched" : "")}");
                lastCalls = calls;
            }
        }
    }

    /// <summary>
    /// Waits for the game window to exist, then for it to be destroyed, which the engine does
    /// before the process ends; then closes the log so its launch-time copy is made. A crash
    /// skips this, and the next launch makes the copy instead.
    /// </summary>
    private void WatchForExit()
    {
        while (GameThread.Window == 0)
        {
            Thread.Sleep(1000);
        }

        while (GameThread.Window != 0)
        {
            Thread.Sleep(500);
        }

        Log.Info("game window gone; closing the log");
        Shutdown();
        Log.Close();
    }

    /// <summary>Stops the background work. Called from the exit watch; a host that has a real exit moment may call it too.</summary>
    public void Shutdown()
    {
        foreach (var action in _shutdown)
        {
            try
            {
                action();
            }
            catch (Exception e) { Log.Error($"shutdown: {e}"); }
        }

        DespawnP2PServer();
    }
}

/// <summary>Wine detection by the exported version function, which every Wine and Proton ntdll has.</summary>
public static class Wine
{
    private static readonly Lazy<string?> s_version = new(() =>
    {
        nint ntdll = Kernel32.GetModuleHandle("ntdll.dll");
        if (ntdll == 0)
        {
            return null;
        }

        nint fn = Kernel32.GetProcAddress(ntdll, "wine_get_version");
        if (fn == 0)
        {
            return null;
        }

        unsafe
        {
            return Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint>)fn)());
        }
    });

    public static bool IsWine => s_version.Value != null;
    public static string? Version => s_version.Value;
}
