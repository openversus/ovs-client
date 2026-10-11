using System.Globalization;
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
using OpenVersus.P2P;

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
    /// <summary>The headers added to the game's requests to the OpenVersus server; null until the hooks are applied. Any part of the client can add a rule.</summary>
    public RequestHeaders? RequestHeaders { get; private set; }
    /// <summary>The HTTP transport for the server, sending <see cref="ServerIdentity"/>'s headers; a host may replace it before <see cref="Initialize"/>.</summary>
    public IHttpTransport Http { get; set; }

    private string _pluginPath;
    /// <summary>Where updates install: plugins/OpenVersus/ when the loader loads it, else beside the plugin.</summary>
    private string _installDirectory;

    private readonly nint _module;
    private readonly List<Action> _shutdown = [];
    /// <summary>The rollback node running beside the game; null when none was started.</summary>
    private RollbackNode? _node;
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
            _shutdown.Add(() => FriendlyFireHooks.ReportCost("game closing"));
            _shutdown.Add(matchRules.Dispose);
        }

        ApplyHooks();
        GameThread.Attach(Log);
        StartRollbackNode(isGame);
        Objects = new ObjectFinder(Image, ProcessMemory.Instance, EngineNames.Instance, Log, tryObjectArray: Status.UeFuncs);
        StockRulesHooks.Attach(Objects);
        StartBackgroundWork();
        return true;
    }

    /// <summary>
    /// <see cref="PakUpdate.ExperimentalReport"/> for this install (the pak updater's folders), or "" without a local
    /// AppData folder.
    /// </summary>
    private string ExperimentalPaksReport()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            return "";
        }

        string saved = Path.Combine(localAppData, "MultiVersus", "Saved");
        string report = PakUpdate.ExperimentalReport(Path.Combine(saved, "Paks"), Path.Combine(saved, Layout.FolderName, "PakHashes.txt"), Log);
        Log.Info($"[Paks] Experimental paks reported to the server: {(report.Length == 0 ? "none" : report.Split(';').Length.ToString())}");
        return report;
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

    /// <summary>
    /// Starts the rollback node beside the game (<see cref="RollbackNode"/>) when the setting is on and
    /// the game's server connection is pointed at an OpenVersus server; the node is told that server.
    /// Its port reaches the server with the identity registration, which waits for it.
    /// </summary>
    private void StartRollbackNode(bool isGame)
    {
        if (!Settings.RollbackNode)
        {
            Log.Info("[Node] Not started: [Features] RollbackNode is off");
            return;
        }

        if (!isGame || !Settings.EnableServerProxy || string.IsNullOrEmpty(Settings.ServerUrl))
        {
            Log.Info("[Node] Not started: the game's server connection is not pointed at an OpenVersus server");
            return;
        }

        ulong port = Settings.P2PRegistryPort;
        if (RendezvousAddress.PortOrDefault(port) != port)
        {
            Log.Warn($"[Node] [Server.Game] P2PRegistryPort = {port} is not a port from 1 to 65535; using {RendezvousAddress.DefaultPort}");
        }

        string? rendezvous = RendezvousAddress.Normalize(Settings.P2PRegistry, port);
        if (rendezvous == null)
        {
            Log.Warn($"[Node] [Server.Game] P2PRegistry = \"{Settings.P2PRegistry}\" is not a host name (the port goes in P2PRegistryPort); the node runs without a rendezvous");
            rendezvous = "";
        }

        var node = new RollbackNode(Directory, Settings.ServerUrl, rendezvous, Wine.IsWine, Log, Wine.IsWine ? Wine.UnixPath : null);
        if (node.Start())
        {
            _node = node;
            _shutdown.Add(node.Stop);
        }
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
            // A reidentify from the server: a new Steam ticket, the identity as it stands (IdentityRegistration.Run filled it).
            var poller = new NotificationPoller(Settings.ServerUrl, Http, Objects, Image, Log,
                reidentify: () => IdentityRegistration.Reidentify(Env!, Settings.ServerUrl, Http, ServerIdentity, Log));
            poller.Start();
            _shutdown.Add(poller.Stop);

            // The Beta Speed a match with the mutator runs at is the server's: every player and the rollback server must agree.
            Start("OVS client settings", () => new ClientSettingsFetch(Settings.ServerUrl, Http, Log, GameSpeedHooks.SetBetaSpeed).Run());
        }

        if (Settings.EnableServerProxy && !string.IsNullOrEmpty(Settings.ServerUrl))
        {
            // The Epic account ID token from the game's own EOS platform, once the game is logged into an Epic account
            // (an Epic Games Store launch): registered as the proof of this client's Epic id.
            var image = Image;
            var epicEnv = Env!;
            var epicIdentity = ServerIdentity;
            _shutdown.Add(EpicIdentity.Restore);
            Start("OVS epic", () =>
            {
                if (EpicIdentity.Probe(image, epicEnv.EpicId, Log)?.Token is { Length: > 0 } token)
                {
                    IdentityRegistration.RegisterEpicToken(epicEnv, token, Settings.ServerUrl, Http, epicIdentity, Log);
                }
            });
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

        if (Status.PvPBotsOff)
        {
            Start("OVS PvPBots", () => PvPBotsPatch.Run(Log));
        }

        var env = Env!;
        var serverIdentity = ServerIdentity;
        var node = _node;
        Start("OVS identity", () => IdentityRegistration.Run(env, Settings.ServerUrl, Http, serverIdentity, Log,
            waitForNodePort: node == null ? null : timeout => node.WaitForPort(timeout)));

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
            GameSpeedHooks.Attach(Log, Settings.LabGameSpeedPercent);
            Status.CreatorCredits = Apply("Creator Credits", c, CreatorCreditHooks.Apply);
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

        // Not a setting: the game's WB telemetry sends every click, with the player's session token,
        // IP and Steam id, to a third party. It is stopped for everyone, in layers, so one pattern
        // going missing does not bring it back: the Store's analytics return at once, the recorder
        // records nothing, and nothing is sent.
        Status.TelemetryOff = Apply("Telemetry", c, TelemetryPatch.Apply);
        Status.TelemetryRecordOff = Apply("TelemetryRecord", c, TelemetryPatch.ApplyRecord);
        Status.TelemetryShopOff = Apply("TelemetryShop", c, TelemetryPatch.ApplyShop);

        // Not a setting: 1v1 and 2v2 queues wait for a real opponent instead of switching to a bot
        // match after about two minutes. Casual and arena keep their bot fallback.
        Status.PvPBotsOff = Apply("PvPBots", c, PvPBotsPatch.Apply);

        // Not a setting: the identity token on the game's login is what ties a player to their own
        // account rather than to whoever last registered from their IP. The game's /access has no
        // token of its own before login, so only-if-missing leaves a token it does have alone.
        var identity = ServerIdentity;
        var headers = new RequestHeaders([Settings.ServerUrl, Settings.ProdServerUrl], Log);
        headers.Add(new HeaderRule(RequestHeaders.HydraAccessToken, () => identity.Token, Path: "/access", OnlyIfMissing: true));
        headers.Add(new HeaderRule(RequestHeaders.OvsIdentity, () => identity.Token));
        // Not a setting: the experimental paks this game mounted, for the queues that need them. Taken once, at the first
        // request to the server, after the startup update has installed what it installs.
        var experimentalPaks = new Lazy<string>(ExperimentalPaksReport);
        headers.Add(new HeaderRule(RequestHeaders.OvsPaks, () => experimentalPaks.Value));
        // Not a setting: the Beta Speed this game runs (the server's, once ClientSettingsFetch has it), for the queues that run it.
        headers.Add(new HeaderRule(RequestHeaders.OvsBetaSpeed, static () => GameSpeedHooks.BetaSpeedPercent.ToString(CultureInfo.InvariantCulture)));
        RequestHeaders = headers;
        Status.RequestHeaders = Apply("RequestHeaders", c, ctx => RequestHeadersHook.Apply(ctx, headers));

        // Not a setting: the server sends hiss_amalgamation's sections as zstd only to clients from this version on,
        // and the game cannot unpack them without this.
        Status.HydraZstd = Apply("HydraZstd", c, HydraZstdHook.Apply);
        if (Status.HydraZstd)
        {
            // Only now is it known: the server reads this header, not the version, to decide between zstd and zlib.
            headers.Add(new HeaderRule(RequestHeaders.OvsZstd, static () => "1"));
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

    /// <summary>
    /// The Linux path for a Windows path, through kernel32's wine_get_unix_file_name (every Wine and
    /// Proton has it); null outside Wine or when the path has no Linux side. The result is freed from
    /// the process heap, where Wine allocates it.
    /// </summary>
    public static unsafe string? UnixPath(string windowsPath)
    {
        nint kernel32 = Kernel32.GetModuleHandle("kernel32.dll");
        nint fn = kernel32 == 0 ? 0 : Kernel32.GetProcAddress(kernel32, "wine_get_unix_file_name");
        if (fn == 0)
        {
            return null;
        }

        fixed (char* path = windowsPath)
        {
            byte* result = ((delegate* unmanaged<char*, byte*>)fn)(path);
            if (result == null)
            {
                return null;
            }

            string unix = Marshal.PtrToStringUTF8((nint)result) ?? "";
            Kernel32.HeapFree(Kernel32.GetProcessHeap(), 0, (nint)result);
            return unix.Length > 0 ? unix : null;
        }
    }
    /// <summary>The Wine version, or null when not under Wine.</summary>
    public static string? Version => s_version.Value;
}
