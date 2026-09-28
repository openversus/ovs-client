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

    /// <summary>The plugin's log.</summary>
    public Log Log { get; }
    /// <summary>The plugin's folder, where the settings, state and cache files live.</summary>
    public string Directory { get; private set; }
    /// <summary>OpenVersus.toml, as read by <see cref="Initialize"/>.</summary>
    public Settings Settings { get; private set; } = null!;
    /// <summary>OVSState.toml, as read by <see cref="Initialize"/>.</summary>
    public State State { get; private set; } = null!;
    /// <summary>Which patches and hooks took.</summary>
    public HookStatus Status { get; } = new();
    /// <summary>The game executable.</summary>
    public GameImage Image { get; private set; } = null!;
    /// <summary>Finds the ini's patterns in <see cref="Image"/>.</summary>
    public PatternResolver Patterns { get; private set; } = null!;
    /// <summary>Finds the game's objects, for the poller and netstats.</summary>
    public ObjectFinder Objects { get; private set; } = null!;
    /// <summary>The player's identity and hardware fingerprint, collected during <see cref="Initialize"/>.</summary>
    public EnvInfo? Env { get; private set; }
    /// <summary>The install id and token the client's own server calls carry.</summary>
    public ServerIdentity ServerIdentity { get; } = new();
    /// <summary>The HTTP transport for the server, sending <see cref="ServerIdentity"/>'s headers; a host may replace it before <see cref="Initialize"/>.</summary>
    public IHttpTransport Http { get; set; }

    private string _pluginPath;
    /// <summary>Where updates install: plugins/OpenVersus/ when the loader loads it, else beside the plugin.</summary>
    private string _installDirectory;

    private readonly nint _module;
    private readonly List<Action> _shutdown = [];
    /// <summary>Whether the startup update got the server's answer, which makes the background plugin check redundant.</summary>
    private bool _startupCheckAnswered;

    /// <summary>A client for the plugin at <paramref name="pluginPath"/>, loaded as <paramref name="module"/>. Nothing runs until <see cref="Initialize"/>.</summary>
    public Client(Log log, string pluginPath, nint module)
    {
        Log = log;
        Directory = Path.GetDirectoryName(pluginPath)!;
        _pluginPath = pluginPath;
        _installDirectory = Directory;
        _module = module;
        Http = new WinHttpTransport(headers: ServerIdentity.Headers);
    }

    /// <summary>
    /// When the install holds other copies of OpenVersus (<see cref="DuplicatePlugins"/>), retires
    /// all but the newest, tells the player, and closes the game without patching anything: another
    /// copy may already have patched it this launch. Returns false when this is the only copy.
    /// </summary>
    private bool RetireOtherCopies()
    {
        string self = Path.GetFullPath(_pluginPath);
        List<string> others = DuplicatePlugins.Find(self, Directory, Path.GetDirectoryName(Environment.ProcessPath));
        if (others.Count == 0)
        {
            return false;
        }

        var (keep, retire) = DuplicatePlugins.Decide(self, others, DuplicatePlugins.EmbeddedVersion);
        Log.Warn($"[Duplicates] {others.Count} other OpenVersus plugin(s) in this install: {string.Join(", ", others.Select(o => $"{o} ({DuplicatePlugins.EmbeddedVersion(o)?.ToString() ?? "no version"})"))}; keeping {keep}");
        var failed = DuplicatePlugins.Retire(retire);
        foreach (var (path, error) in failed)
        {
            Log.Warn($"[Duplicates] Could not rename {path}: {error}");
        }

        var renamed = retire.Where(r => !failed.Any(f => f.Path == r)).ToList();
        var text = new System.Text.StringBuilder("OpenVersus found more than one copy of itself in this game install, and only one can run at a time.\n\n");
        if (renamed.Count > 0)
        {
            text.Append("These were renamed to .bak and will not load again:\n").AppendJoin("\n", renamed).Append("\n\n");
        }

        if (failed.Count > 0)
        {
            text.Append("These could not be renamed; please delete them by hand:\n").AppendJoin("\n", failed.Select(f => f.Path)).Append("\n\n");
        }

        text.Append("Keeping: ").Append(keep).Append("\n\nThe game will now close. Please launch it again.");
        Log.Info("[Duplicates] Closing the game so only one copy loads next launch");
        User32.MessageBox(0, text.ToString(), OvsVersion.Name, User32.MB_ICONEXCLAMATION);
        Log.Close();
        Firmware.TerminateProcess(Kernel32.GetCurrentProcess(), 0);
        return true;
    }

    /// <summary>
    /// Moves the plugin and its files into plugins/OpenVersus/ beside the game executable when it
    /// runs from anywhere else and the loader's settings say that folder is loaded
    /// (<see cref="LoaderConfig"/>); otherwise it stays where it is. This launch carries on from
    /// the new folder, except the log, which stays where it was opened. Updates install into the
    /// same folder.
    /// </summary>
    private void MoveHome()
    {
        string? game = Path.GetDirectoryName(Environment.ProcessPath);
        if (game == null)
        {
            return;
        }

        string home = Path.Combine(game, "plugins", Layout.FolderName);
        bool loaded = LoaderConfig.LoadsPluginSubfolders(game, out string why);
        if (!loaded)
        {
            if (!Layout.SameFolder(Directory, home))
            {
                Log.Info($"[Layout] Staying in {Directory} rather than moving to {home}: {why}");
            }

            return;
        }

        _installDirectory = home;
        if (Layout.SameFolder(Directory, home))
        {
            return;
        }

        string? moved = Layout.MoveInto(_pluginPath, home, out var notes);
        foreach (string note in notes)
        {
            Log.Info($"[Layout] {note}");
        }

        if (moved == null)
        {
            Log.Warn($"[Layout] Running from {Directory} this launch; updates still install into {home}");
            return;
        }

        _pluginPath = moved;
        Directory = home;
        Log.Info($"[Layout] Running from {home} now ({why}); this launch's log stays at {Log.Path}");
    }

    /// <summary>
    /// Reads the settings, applies every patch and hook they enable and starts the background work.
    /// Each hook's failure is logged and the rest still apply, so this returns true.
    /// </summary>
    public bool Initialize()
    {
        Log.Info($"On Attach Initialize ({OvsVersion.Name} {OvsVersion.Current})");
        if (RetireOtherCopies())
        {
            return true;
        }

        MoveHome();

        string? oldHome = Layout.OldHome(Directory);
        Settings = Settings.Load(Directory, Log, oldHome);
        State = new State(Directory, oldHome).Load();
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

        // The install id is created before the first request to the server, the startup check
        // below, so every call carries it. "" when it cannot be stored, and the check still runs.
        string installId = State.LoadOrCreateInstallId();
        ServerIdentity.InstallId = installId;
        Log.Info($"[OVS] Install identity {(installId.Length > 0 ? "verified" : "unavailable")}");

        // The required update runs before anything is patched: the engine has not opened its
        // paks yet, so they can be replaced. When it installs something it closes the game.
        // Nothing it throws may stop the hooks below; the background check then runs as it
        // does when the server gives no answer.
        if (isGame && !string.IsNullOrEmpty(Settings.ServerUrl))
        {
            try
            {
                _startupCheckAnswered = RunStartupUpdate() != StartupUpdate.Outcome.NoAnswer;
            }
            catch (Exception e)
            {
                Log.Error($"[Update] The startup check failed: {e}");
            }
        }

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
        var cache = new PatternCache(Directory, hash, OvsVersion.Current, Layout.OldHome(Directory));
        Patterns = new PatternResolver(Image, cache, Settings, Log);
        Log.Info("Parsed Settings");

        // Collect Steam/Epic identity, the install id and the hardware fingerprint. They make
        // accounts "sticky", so nobody resets their name and perks when their IP changes or they
        // switch to Proton.
        var runtime = Runtime.Detect();
        Env = new EnvInfo(runtime) { InstallId = installId };
        Log.Info($"[OVS] Runtime: {Runtime.Name(runtime)}; hardware fingerprint {(Env.HardwareId.Length > 0 ? "v2" : "none")}");

        if (Settings.MatchRulesLog)
        {
            // Beside the main log, with the same writer, archive and retention behavior.
            var matchRules = Log.OpenSession(Path.GetDirectoryName(Log.Path)!, "MatchRules", fallbackDirectory: Directory);
            matchRules.MinimumLevel = LogLevel.Information;
            if (matchRules.Notice != null)
            {
                Log.Warn(matchRules.Notice);
            }

            MatchRulesLog.Attach(matchRules);
            _shutdown.Add(matchRules.Dispose);
        }

        ApplyHooks();
        GameThread.Attach(Log);
        SpawnP2PServer();
        Objects = new ObjectFinder(Image, ProcessMemory.Instance, EngineNames.Instance, Log, tryObjectArray: Status.UeFuncs);
        StockRulesHooks.Attach(Objects);
        StartBackgroundWork();
        return true;
    }

    /// <summary>
    /// <see cref="StartupUpdate"/> for this install: paks in %LOCALAPPDATA%\MultiVersus\Saved\Paks
    /// (skipped when there is no local AppData folder), staging, backup and the hash cache beside it
    /// in Saved\OpenVersus, hand-installed paks moved out of the game's Content\Paks, and the plugin
    /// updater's own checks for a newer .asi.
    /// </summary>
    private StartupUpdate.Outcome RunStartupUpdate()
    {
        // OVS paks live in the game's Saved folder, %LOCALAPPDATA%\MultiVersus\Saved\Paks: one of
        // the folders the engine mounts paks from (verified in game 2026-09-26), always writable,
        // and left alone by Steam. The updater's own files sit beside it in Saved\OpenVersus, on
        // the same drive, so installing is a rename. Under Proton this is the prefix's AppData.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? saved = string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "MultiVersus", "Saved");
        if (saved == null)
        {
            Log.Warn("[Update] No local AppData folder; paks cannot be updated");
        }

        // <game>/MultiVersus/Binaries/Win64/<exe>: hand-installed paks were in <game>/MultiVersus/Content/Paks.
        string? exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        string? gameDirectory = exeDirectory == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(exeDirectory));
        string? contentPaks = gameDirectory == null ? null : Path.Combine(gameDirectory, "Content", "Paks");

        var download = new WinHttpTransport(useSystemProxy: true);
        var plugins = new AutoUpdate(Settings.ServerUrl, _pluginPath, _installDirectory, Http, download, Log, Log.Close);
        string work = saved == null ? "" : Path.Combine(saved, Layout.FolderName);
        var paks = saved == null ? null : new PakUpdate(Path.Combine(saved, "Paks"), Path.Combine(work, "update-staging"),
            Path.Combine(work, "pak-backup"), Path.Combine(work, "PakHashes.txt"), download, Log, Settings.ReleaseOwner)
        {
            LegacyDirectory = contentPaks,
            LegacyBackupDirectory = Path.Combine(work, "old-content-paks"),
        };
        if (paks != null && paks.ReleaseOwner != PakUpdate.DefaultOwner)
        {
            Log.Warn($"[Update] Testing: paks may download from {paks.ReleaseOwner}'s releases ([Settings.Debug] ReleaseOwner)");
        }
        var state = State;
        return new StartupUpdate(Settings.ServerUrl, Settings.AutoUpdate, Http, plugins, paks, Log, Log.Close, state.SetUpdateNotice).Run();
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

        string? updated = State.TakeUpdateNotice();
        if (updated != null)
        {
            Log.Info($"[Update] The last launch installed an update: {updated}");
        }

        if (Status.UeFuncs && Status.Dialog)
        {
            var state = State;
            var problem = Settings.Problem;
            Start("OVS startup notices", () => StartupNotices.Run(state, problem, Log, updated));
        }

        var env = Env!;
        var serverIdentity = ServerIdentity;
        Start("OVS identity", () => IdentityRegistration.Run(env, Settings.ServerUrl, Http, serverIdentity, Log));

        if (Settings.AutoUpdate && _startupCheckAnswered)
        {
            Log.Info("[AutoUpdate] Auto-update is enabled; the startup check already asked the server for this launch.");
        }
        else if (Settings.AutoUpdate)
        {
            Log.Info("[AutoUpdate] Auto-update is enabled. OVS will check for updates automatically and download/apply them when available.");
            var update = new AutoUpdate(Settings.ServerUrl, _pluginPath, _installDirectory, Http, new WinHttpTransport(useSystemProxy: true), Log, Log.Close);
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
            var stats = new NetStatsLogger(Objects, Log, openMatchLog: _ =>
            {
                var matchLog = Log.OpenSession(logDirectory, "NetStats", fallbackDirectory: fallback);
                matchLog.MinimumLevel = LogLevel.Information;
                if (matchLog.Notice != null)
                {
                    Log.Warn(matchLog.Notice);
                }

                return matchLog;
            }, nameMatchLog: (matchLog, info) =>
            {
                // The name is applied when the log is archived (closed), so a description read
                // seconds into the match still names the file.
                if (matchLog is Log session)
                {
                    session.ArchiveSuffix = info.FileSuffix;
                }
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
            Status.Stocks = Apply("Stock Rules", c, StockRulesHooks.Apply);
            Status.FriendlyFire = Apply("Friendly Fire", c, FriendlyFireHooks.Apply);
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

    /// <summary>Whether this process runs under Wine or Proton.</summary>
    public static bool IsWine => s_version.Value != null;
    /// <summary>The Wine version, or null when not under Wine.</summary>
    public static string? Version => s_version.Value;
}
