using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Stock rules in the game (<see cref="StockRules"/>): in Free For All, and in a 2v2 with the
/// Individual Stocks mutator, every fighter has lives from the server's ringout count and a
/// fighter out of lives is not respawned; in Free For All the last one standing wins. Entry hooks
/// on four native functions of this build; the C++ client's StockDiagnostics, without its
/// diagnostics. The game mode's first character is also where each match's settings are read, so
/// <see cref="FriendlyFireHooks"/> is told of every match from here.
/// <list type="bullet">
/// <item>AMvsGameModeBase::RegisterCharacter: a new game mode starts a match (the mode, lives and
/// mutators come from UMvsGameplayConfig); each fighter is recorded.</item>
/// <item>AMvsGameModeBase::PlayerDied: on a fighter's first death RespawnsRemaining (-1, unlimited)
/// becomes lives - 1; a death at 0 puts it out; in FFA, when one fighter is left, EndMatch(its team).</item>
/// <item>APfgFixedPawn::DoRespawn: skipped for a fighter at 0, which the native code would revive.</item>
/// <item>UMvsGameModeDefaultGameEndHandlerComponent::AttemptEndMatch: in FFA the game's score-based
/// end is skipped until these rules end the match.</item>
/// </list>
/// Every peer runs the same rules on the same simulated state, so they agree. The hooks run on the
/// game thread; the HUD update is posted to it to run after the simulation step.
/// </summary>
public static unsafe class StockRulesHooks
{
    // The patterns live here, not in OpenVersus.toml: the stock rules are the server's to turn on, not a player's.
    // Each is the start of the function, whole instructions from the game's only build with rel32 targets and
    // RIP-relative displacements as wildcards, and matches once there (StockRulesHooksTests has the bytes). The
    // functions are the C++ client's (StockDiagnostics.cpp): RVAs 0x02966870, 0x02966E20, 0x011E4F70, 0x02967BE0,
    // 0x011FC980 and 0x02966FC0.
    /// <summary>AMvsGameModeBase::PlayerDied.</summary>
    internal const string PlayerDiedPattern = "48 85 D2 0F 84 ? ? ? ? 48 8B C4 48 89 48 08 55 41 54 41 56";
    /// <summary>AMvsGameModeBase::RegisterCharacter.</summary>
    internal const string RegisterCharacterPattern = "40 53 56 57 48 81 EC C0 00 00 00 48 8B 05 ? ? ? ? 48 33 C4 48 89 84 24 B0 00 00 00 48 8B FA";
    /// <summary>APfgFixedPawn::DoRespawn.</summary>
    internal const string DoRespawnPattern = "8B 81 80 03 00 00 85 C0 7E 08 FF C8 89 81 80 03 00 00";
    /// <summary>UMvsGameModeDefaultGameEndHandlerComponent::AttemptEndMatch.</summary>
    internal const string AttemptEndMatchPattern = "40 56 48 83 EC 50 80 B9 E0 00 00 00 00 48 8B F1";
    /// <summary>APfgFixedPawn::SetRespawnsRemaining: mov [rcx+0x380], edx; ret, and the padding after it.</summary>
    internal const string SetRespawnsRemainingPattern = "89 91 80 03 00 00 C3 CC CC CC CC CC CC CC CC CC";
    /// <summary>AMvsGameModeBase::Respawn.</summary>
    internal const string RespawnPattern = "48 85 D2 0F 84 ? ? ? ? 55 41 56 41 57 48 8D 6C 24 B9 48 81 EC 00 01 00 00";

    // The instructions the entry hooks move, at least five bytes each.
    // test rdx, rdx; je rel32
    internal static readonly byte[] s_playerDiedPrologue = [0x48, 0x85, 0xD2, 0x0F, 0x84, 0x9A, 0x05, 0x00, 0x00];
    // push rbx; push rsi; push rdi; sub rsp, 0xC0
    internal static readonly byte[] s_registerCharacterPrologue = [0x40, 0x53, 0x56, 0x57, 0x48, 0x81, 0xEC, 0xC0, 0x00, 0x00, 0x00];
    // mov eax, [rcx+0x380]
    internal static readonly byte[] s_doRespawnPrologue = [0x8B, 0x81, 0x80, 0x03, 0x00, 0x00];
    // push rsi; sub rsp, 0x50
    internal static readonly byte[] s_attemptEndMatchPrologue = [0x40, 0x56, 0x48, 0x83, 0xEC, 0x50];
    // test rdx, rdx; je rel32
    internal static readonly byte[] s_respawnPrologue = [0x48, 0x85, 0xD2, 0x0F, 0x84, 0xE3, 0x02, 0x00, 0x00];

    private static delegate* unmanaged<nint, nint, nint, void> s_playerDied;
    private static delegate* unmanaged<nint, nint, void> s_registerCharacter;
    private static delegate* unmanaged<nint, void> s_doRespawn;
    private static delegate* unmanaged<nint, void> s_attemptEndMatch;
    private static delegate* unmanaged<nint, int, void> s_setRespawnsRemaining;
    private static delegate* unmanaged<nint, nint, void> s_respawn;
    // Fighters kept dead and taken off the camera's framing; a rematch that reuses one puts it back.
    private static readonly HashSet<nint> s_offCamera = [];
    private static CameraTargets? s_camera;

    private static ILogger? s_log;
    private static ObjectFinder? s_finder;
    private static nint s_gameMode;
    private static StockRules? s_match;
    private static bool s_endBlockLogged;
    private static HudTargets? s_hud;
    private static nint s_hudLoggedFor;
    private static Timer? s_hudSeedTimer;
    private static int s_hudSeedTicks;
    // How long after a match starts the lives are sent to the HUD once a second: the HUD appears
    // some seconds in (about 12 s in the first tests), and the game zeroes its scores when it does.
    private const int HudSeedSeconds = 20;
    private static long s_hudSequence;
    private static long s_hudApplied;

    /// <summary>
    /// Hooks the five functions. False, with nothing hooked, when a pattern is missing; a
    /// <see cref="PatchException"/> when this build's code is not what the disassembly found.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        c.Log.Info("==Stock Rules==");
        var image = c.Image;
        var playerDied = c.Patterns.Find("StocksPlayerDied", PlayerDiedPattern);
        var registerCharacter = c.Patterns.Find("StocksRegisterCharacter", RegisterCharacterPattern);
        var doRespawn = c.Patterns.Find("StocksDoRespawn", DoRespawnPattern);
        var attemptEndMatch = c.Patterns.Find("StocksAttemptEndMatch", AttemptEndMatchPattern);
        var setRespawns = c.Patterns.Find("StocksSetRespawnsRemaining", SetRespawnsRemainingPattern);
        var respawn = c.Patterns.Find("StocksRespawn", RespawnPattern);
        if (!playerDied.Found || !registerCharacter.Found || !doRespawn.Found || !attemptEndMatch.Found || !setRespawns.Found || !respawn.Found)
        {
            c.Log.Error("Stock rules: a function they need was not found; nothing is hooked and FFA and 2v2 Individual Stocks will not work");
            return false;
        }

        s_setRespawnsRemaining = (delegate* unmanaged<nint, int, void>)setRespawns.Address;
        GameFunctions.Register("APfgFixedPawn::SetRespawnsRemaining", setRespawns, FunctionSource.Pattern, "StocksSetRespawnsRemaining", "void APfgFixedPawn::SetRespawnsRemaining(APfgFixedPawn* this, int32 limit)", image);

        s_playerDied = (delegate* unmanaged<nint, nint, nint, void>)Hook(image, "AMvsGameModeBase::PlayerDied", playerDied, s_playerDiedPrologue, (nint)(delegate* unmanaged<nint, nint, nint, void>)&PlayerDied, "void AMvsGameModeBase::PlayerDied(AMvsGameModeBase* this, AMvsFixedCharacter* victim, AMvsFixedCharacter* attacker)", endsInConditionalJump: true);
        s_registerCharacter = (delegate* unmanaged<nint, nint, void>)Hook(image, "AMvsGameModeBase::RegisterCharacter", registerCharacter, s_registerCharacterPrologue, (nint)(delegate* unmanaged<nint, nint, void>)&RegisterCharacter, "void AMvsGameModeBase::RegisterCharacter(AMvsGameModeBase* this, APfgFixedPawn* character)");
        s_doRespawn = (delegate* unmanaged<nint, void>)Hook(image, "APfgFixedPawn::DoRespawn", doRespawn, s_doRespawnPrologue, (nint)(delegate* unmanaged<nint, void>)&DoRespawn, "void APfgFixedPawn::DoRespawn(APfgFixedPawn* this)");
        s_respawn = (delegate* unmanaged<nint, nint, void>)Hook(image, "AMvsGameModeBase::Respawn", respawn, s_respawnPrologue, (nint)(delegate* unmanaged<nint, nint, void>)&Respawn, "void AMvsGameModeBase::Respawn(AMvsGameModeBase* this, AMvsFixedCharacter* victim)", endsInConditionalJump: true);
        s_attemptEndMatch = (delegate* unmanaged<nint, void>)Hook(image, "UMvsGameModeDefaultGameEndHandlerComponent::AttemptEndMatch", attemptEndMatch, s_attemptEndMatchPrologue, (nint)(delegate* unmanaged<nint, void>)&AttemptEndMatch, "void UMvsGameModeDefaultGameEndHandlerComponent::AttemptEndMatch(UMvsGameModeDefaultGameEndHandlerComponent* this)");

        c.Log.Success("Stock rules hooked");
        return true;
    }

    /// <summary>The object finder the rules read the gameplay config and HUD through; until it is attached they stay off.</summary>
    public static void Attach(ObjectFinder finder) => s_finder = finder;

    private static nint Hook(GameImage image, string name, PatternHit hit, byte[] prologue, nint hook, string declaration, bool endsInConditionalJump = false)
    {
        nint gateway = EntryHook.Install(hit.Address, prologue, hook, endsInConditionalJump);
        GameFunctions.Register(name, gateway, FunctionSource.Pattern, $"{hit.Name} at 0x{hit.Address:X}, entry hooked; this is the gateway to the original", declaration, image);
        return gateway;
    }

    [UnmanagedCallersOnly]
    private static void RegisterCharacter(nint gameMode, nint character)
    {
        s_registerCharacter(gameMode, character);
        HookGuard.Run("StocksRegisterCharacter", (gameMode, character), static s => OnRegisterCharacter(s.gameMode, s.character));
    }

    [UnmanagedCallersOnly]
    private static void PlayerDied(nint gameMode, nint victim, nint attacker)
    {
        int respawns = HookGuard.Run("StocksPlayerDied", victim, static v => BeforeDeath(v), int.MinValue);
        s_playerDied(gameMode, victim, attacker);
        if (respawns != int.MinValue)
        {
            HookGuard.Run("StocksPlayerDied", (gameMode, victim, respawns), static s => AfterDeath(s.gameMode, s.victim, s.respawns));
        }
    }

    [UnmanagedCallersOnly]
    private static void DoRespawn(nint pawn)
    {
        bool skip = HookGuard.Run("StocksDoRespawn", pawn, static p => s_match is { } match && TryReadInt(p + Mvs.PawnRespawnsRemaining, out int respawns) && match.SkipsRespawn(p, respawns), false);
        if (!skip)
        {
            s_doRespawn(pawn);
        }
    }

    /// <summary>
    /// The respawn timer of a fighter with no respawns left: the game would destroy the pawn
    /// (DestroySpawnedActor, RespawnLimitReached), which takes that player's input source out of the
    /// netcode session. The local client then becomes a spectator: it stops sending input, stalls on
    /// server-confirmed frames and runs behind, which is where online stock matches desynced. Skipped
    /// for a fighter out of lives, so it just stays dead. Decided from the pawn's RespawnsRemaining,
    /// which is rollback state, so every client decides alike.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void Respawn(nint gameMode, nint victim)
    {
        bool keepDead = HookGuard.Run("StocksRespawn", victim, static v => s_match is { } match && match.IsFighter(v) && TryReadInt(v + Mvs.PawnRespawnsRemaining, out int respawns) && respawns == 0, false);
        if (keepDead)
        {
            bool first = HookGuard.Run("StocksRespawnCamera", victim, static v => { lock (s_offCamera) { return s_offCamera.Add(v); } }, false);
            if (first)
            {
                MatchRulesLog.Line("stocks: a fighter out of lives is kept dead (not destroyed) and taken off the camera");
                SetOnCamera(victim, on: false);
            }
            return;
        }

        s_respawn(gameMode, victim);
    }

    [UnmanagedCallersOnly]
    private static void AttemptEndMatch(nint component)
    {
        bool block = HookGuard.Run("StocksAttemptEndMatch", component, static _ => s_match is { LastFighterWins: true, Ended: false }, false);
        if (block)
        {
            if (!s_endBlockLogged)
            {
                s_endBlockLogged = true;
                s_log?.Debug("[FFA] the game's score-based match end is held until one fighter is left");
            }

            return;
        }

        s_attemptEndMatch(component);
        HookGuard.Run("StocksAttemptEndMatch", 0, static _ => FriendlyFireHooks.ReportCost("match end"));
    }

    /// <summary>
    /// A new game mode is a new match: read its settings, turn on the stock rules it has, and tell
    /// <see cref="FriendlyFireHooks"/>. Then record the character if it is a fighter.
    /// </summary>
    private static void OnRegisterCharacter(nint gameMode, nint character)
    {
        var finder = s_finder;
        if (finder == null || character == 0)
        {
            return;
        }

        if (gameMode != s_gameMode)
        {
            s_gameMode = gameMode;
            var settings = ReadSettings(finder);
            s_match = settings != null ? StartMatch(finder, settings) : null;
            FriendlyFireHooks.StartMatch(finder, gameMode, settings);
            s_endBlockLogged = false;
            if (s_match is { LastFighterWins: true })
            {
                StartHudSeeding();
            }
        }

        if (s_match is not { } match || !IsFighter(finder, character))
        {
            return;
        }

        nint data = character + Mvs.FixedCharacterGameplayPlayerData;
        match.Register(character, ReadInt(data + Mvs.GameplayPlayerDataPlayerIndex), ReadInt(data + Mvs.GameplayPlayerDataTeamIndex));
        bool wasOffCamera;
        lock (s_offCamera)
        {
            wasOffCamera = s_offCamera.Remove(character);
        }
        if (wasOffCamera)
        {
            SetOnCamera(character, on: true);
        }
    }

    /// <summary>
    /// Takes a fighter kept dead off the camera's framing (or puts it back, for a rematch that reuses
    /// it), as destroying it did: otherwise the camera keeps zooming out to frame where it died.
    /// Finding the camera walks the object array, so that runs on a pool thread (cached per match)
    /// and only the call is posted to the game thread. The camera is each client's own view, not
    /// gameplay, so this cannot desync.
    /// </summary>
    private static void SetOnCamera(nint pawn, bool on)
    {
        ThreadPool.QueueUserWorkItem(static s => HookGuard.Run("StocksCamera", s, static s =>
        {
            if (FindCamera(s.GameMode) is { } camera)
            {
                GameThread.Post("StocksCamera", () => HookGuard.Run("StocksCamera", (camera, s.Pawn, s.On), static t => CallCamera(t.camera, t.Pawn, t.On)));
            }
        }), (Pawn: pawn, On: on, GameMode: s_gameMode), preferLocal: false);
    }

    /// <summary>The match's cameras and their functions: cached while they are alive, otherwise found again. Off the game thread.</summary>
    private static CameraTargets? FindCamera(nint gameMode)
    {
        var finder = s_finder;
        if (finder == null)
        {
            return null;
        }

        if (s_camera is { } cached && cached.GameMode == gameMode && cached.Cameras.Length > 0 && cached.Cameras.All(finder.IsLive))
        {
            return cached;
        }

        var camera = new CameraTargets(
            gameMode,
            finder.FindInstancesOfClass(finder.FindClass("MvsCamera")).ToArray(),
            Reflection.Find(finder, finder.Image, "MvsCamera", "UnRegisterActorWithCamera"),
            Reflection.Find(finder, finder.Image, "MvsCamera", "RegisterActorWithCamera"),
            Reflection.Find(finder, finder.Image, "MvsCamera", "IsActorRegistered"));
        s_camera = camera;
        if (camera.Cameras.Length == 0 || !TakesActorFirst(camera.Unregister))
        {
            s_log?.Warn($"[Stocks] camera not found ({camera.Cameras.Length} camera(s)); a fighter out of lives stays framed");
        }
        return camera;
    }

    /// <summary>
    /// UnRegisterActorWithCamera(AActor*) or RegisterActorWithCamera(AActor*, int32 OptionalPlayerIndexCamera)
    /// on each camera, the actor first in both (checked against the reflected parameters). Registering
    /// skips a camera that already frames the actor. Game thread.
    /// </summary>
    private static void CallCamera(CameraTargets camera, nint pawn, bool on)
    {
        var function = on ? camera.Register : camera.Unregister;
        if (s_finder is not { } finder || !finder.IsLive(pawn) || !TakesActorFirst(function))
        {
            return;
        }

        var sig = function!.Signature.Reflected!;
        Span<byte> parameters = stackalloc byte[sig.ParmsSize];
        int calls = 0;
        foreach (nint cam in camera.Cameras.Where(finder.IsLive))
        {
            if (on && IsRegistered(camera, cam, pawn))
            {
                continue;
            }

            parameters.Clear();
            BitConverter.TryWriteBytes(parameters, (long)pawn);
            if (on && sig.Parameters.FirstOrDefault(p => !p.IsReturn && p.Type == "IntProperty") is { } index)
            {
                BitConverter.TryWriteBytes(parameters[index.Offset..], -1);
            }

            Reflection.Invoke(function, cam, parameters);
            calls++;
        }

        s_log?.Debug($"[Stocks] fighter {(on ? "back on" : "off")} camera ({calls} of {camera.Cameras.Length} cameras)");
    }

    private static bool IsRegistered(CameraTargets camera, nint cam, nint pawn)
    {
        if (!TakesActorFirst(camera.IsRegistered) || camera.IsRegistered!.Signature.Reflected is not { ReturnValueOffset: >= 8 } sig)
        {
            return false;
        }

        Span<byte> parameters = stackalloc byte[sig.ParmsSize];
        parameters.Clear();
        BitConverter.TryWriteBytes(parameters, (long)pawn);
        Reflection.Invoke(camera.IsRegistered, cam, parameters);
        return parameters[sig.ReturnValueOffset] != 0;
    }

    private static bool TakesActorFirst(GameFunction? function) =>
        function?.Signature.Reflected is { ParmsSize: >= 8 and <= 16 } sig
        && sig.Parameters.FirstOrDefault(p => !p.IsReturn) is { Type: "ObjectProperty", Offset: 0 };

    private sealed record CameraTargets(nint GameMode, nint[] Cameras, GameFunction? Unregister, GameFunction? Register, GameFunction? IsRegistered);

    private static nint TryReadPointer(nint address) => address != 0 && CodeWriter.TryRead(address, out nint value) ? value : 0;

    /// <summary>The match's settings from UMvsGameplayConfig, or null (logged) when they cannot be read.</summary>
    private static MatchSettings? ReadSettings(ObjectFinder finder)
    {
        nint config = finder.FindInstanceOfClass(finder.FindClass("MvsGameplayConfig"));
        if (!MatchSettings.TryRead(finder.Memory, config, out var settings))
        {
            s_log?.Warn("[Stocks] gameplay config unreadable; stock rules and friendly fire off for this match");
            MatchRulesLog.Line("match: gameplay config unreadable");
            return null;
        }

        MatchRulesLog.Line($"match: {settings}");
        return settings;
    }

    private static StockRules? StartMatch(ObjectFinder finder, MatchSettings settings)
    {
        if (StockRules.For(settings) is not { } match)
        {
            return null;
        }

        // Resolved now, not at the final death, so the object scan happens while the match loads.
        if (match.LastFighterWins && Reflection.Find(finder, finder.Image, "MvsGameModeBase", "EndMatch") == null)
        {
            s_log?.Warn("[Stocks] AMvsGameModeBase::EndMatch not found; stock rules off for this match");
            return null;
        }

        s_log?.Info($"[Stocks] {match.Name} stock rules on: {match.Lives} lives each (server ringouts {settings.NumRingouts})");
        MatchRulesLog.Line($"stocks: {match.Name}, {match.Lives} lives each");
        return match;
    }

    /// <summary>
    /// Before the game handles a death: on a fighter's first, RespawnsRemaining goes from unlimited
    /// to lives - 1. Returns what it is then (lives left after this death), or int.MinValue when
    /// the rules are off or the victim is not a fighter.
    /// </summary>
    private static int BeforeDeath(nint victim)
    {
        if (s_match is not { Ended: false } match || !match.IsFighter(victim))
        {
            return int.MinValue;
        }

        if (!TryReadInt(victim + Mvs.PawnRespawnsRemaining, out int respawns))
        {
            return int.MinValue;
        }

        if (respawns == -1)
        {
            s_setRespawnsRemaining(victim, match.InitialRespawns);
            respawns = match.InitialRespawns;
        }

        return respawns;
    }

    private static void AfterDeath(nint gameMode, nint victim, int respawns)
    {
        if (s_match is not { } match)
        {
            return;
        }

        int? winningTeam = match.Died(victim, respawns);
        MatchRulesLog.Line($"stocks: death, {respawns} lives left, {match.EliminatedCount}/{match.FighterCount} out");
        if (respawns == 0)
        {
            s_log?.Info($"[Stocks] fighter out ({match.EliminatedCount}/{match.FighterCount})");
        }

        // The HUD shows each FFA player's own score; a 2v2 HUD shows team scores, left to the game.
        if (match.LastFighterWins)
        {
            PostLivesToHud(match);
        }

        if (winningTeam is not int team)
        {
            return;
        }

        var endMatch = GameFunctions.Find("MvsGameModeBase::EndMatch");
        if (endMatch == null)
        {
            return;
        }

        s_log?.Info($"[FFA] one fighter left; ending the match for team {team}");
        Span<byte> parameters = stackalloc byte[4];
        BitConverter.TryWriteBytes(parameters, team);
        Reflection.Invoke(endMatch, gameMode, parameters);
    }

    /// <summary>
    /// Shows lives in the HUD's score for each player. Finding the HUD walks the whole object
    /// array (about 150,000 objects), too slow for the game thread: it runs on a pool thread, once
    /// per match (the objects are cached and checked for being alive after that), and only the
    /// calls are posted to the game thread, after the simulation step. Whether the game overwrites
    /// the score afterwards is what the first matches are to show.
    /// </summary>
    private static void PostLivesToHud(StockRules match)
    {
        var update = new HudUpdate(s_gameMode, match.LivesLeft(), match.PlayerIndexes.ToArray(), match.Lives, Interlocked.Increment(ref s_hudSequence));
        ThreadPool.QueueUserWorkItem(static u => HookGuard.Run("FfaLivesHud", u, static u =>
        {
            if (FindHud(u.GameMode) is { } hud)
            {
                GameThread.Post("FfaLivesHud", () => HookGuard.Run("FfaLivesHud", (hud, u), static s => PushLivesToHud(s.hud, s.u)));
            }
        }), update, preferLocal: false);
    }

    /// <summary>
    /// Sends the lives to the HUD once a second for the first <see cref="HudSeedSeconds"/> seconds of
    /// a match, so the scores show full lives from the start instead of the game's 0 until the first
    /// death. Runs on a timer thread; each send is <see cref="PostLivesToHud"/>.
    /// </summary>
    private static void StartHudSeeding()
    {
        s_hudSeedTimer?.Dispose();
        s_hudSeedTicks = 0;
        s_hudSeedTimer = new Timer(static _ => HookGuard.Run("FfaLivesHudSeed", 0, static _ => SeedHudTick()), null, 1000, 1000);
    }

    private static void SeedHudTick()
    {
        if (s_match is not { Ended: false } match || ++s_hudSeedTicks > HudSeedSeconds)
        {
            Interlocked.Exchange(ref s_hudSeedTimer, null)?.Dispose();
            return;
        }

        PostLivesToHud(match);
    }

    /// <summary>The HUD objects for this match: cached while they are alive, otherwise found again. Off the game thread.</summary>
    private static HudTargets? FindHud(nint gameMode)
    {
        var finder = s_finder;
        if (finder == null)
        {
            return null;
        }

        if (s_hud is { } cached && cached.GameMode == gameMode && cached.Broker != 0 && cached.Widgets.Length > 0 && IsAlive(finder, cached))
        {
            return cached;
        }

        var hud = new HudTargets(
            gameMode,
            finder.FindInstanceOfClass(finder.FindClass("UI_Broker_C")),
            finder.FindInstancesOfClass(finder.FindClass("UI_IGv3_PlayerScore_C")).ToArray(),
            Reflection.Find(finder, finder.Image, "UI_Broker_C", "SetMaxScoreForPlayer"),
            Reflection.Find(finder, finder.Image, "UI_Broker_C", "SetScoreForPlayer"),
            Reflection.Find(finder, finder.Image, "UI_IGv3_PlayerScore_C", "OnScore"));
        s_hud = hud;
        if (hud.Widgets.Length == 0 || s_hudLoggedFor == gameMode)
        {
            return hud;
        }

        s_hudLoggedFor = gameMode;
        s_log?.Info($"[FFA] HUD found: broker={(hud.Broker != 0 ? "yes" : "no")} score widgets={hud.Widgets.Length} "
            + $"setMax={TakesPlayerAndScore(hud.SetMax)} setScore={TakesPlayerAndScore(hud.SetScore)} onScore={TakesPlayerAndScore(hud.OnScore)}");
        return hud;
    }

    private static bool IsAlive(ObjectFinder finder, HudTargets hud) =>
        finder.IsLive(hud.Broker) && hud.Widgets.All(finder.IsLive);

    private static void PushLivesToHud(HudTargets hud, HudUpdate u)
    {
        // A later death's update may already have been applied; an older one must not undo it.
        if (u.Sequence < s_hudApplied || s_finder is not { } finder || !IsAlive(finder, hud))
        {
            return;
        }

        s_hudApplied = u.Sequence;
        int highest = u.Players.Select(p => u.Lives[p]).DefaultIfEmpty(0).Max();
        bool tied = u.Players.Count(p => u.Lives[p] == highest) > 1;
        int calls = 0;
        Span<byte> parameters = stackalloc byte[16];
        foreach (int player in u.Players)
        {
            // (int32 player, int32 score, bool leader, bool tied): the C++ client's layout, checked
            // against each function's reflected parameters before the call.
            parameters.Clear();
            BitConverter.TryWriteBytes(parameters, player);
            BitConverter.TryWriteBytes(parameters[4..], u.MaxLives);
            calls += Call(hud.SetMax, hud.Broker, parameters);

            BitConverter.TryWriteBytes(parameters[4..], u.Lives[player]);
            parameters[8] = (byte)(u.Lives[player] == highest ? 1 : 0);
            parameters[9] = (byte)(u.Lives[player] == highest && tied ? 1 : 0);
            calls += Call(hud.SetScore, hud.Broker, parameters);
            foreach (nint widget in hud.Widgets)
            {
                calls += Call(hud.OnScore, widget, parameters);
            }
        }

        s_log?.Debug($"[FFA] lives to HUD: {string.Join(" ", u.Players.Select(p => $"p{p}={u.Lives[p]}"))}; calls={calls}");
    }

    private static int Call(GameFunction? function, nint target, Span<byte> parameters)
    {
        if (target == 0 || !TakesPlayerAndScore(function))
        {
            return 0;
        }

        Reflection.Invoke(function!, target, parameters[..function!.Signature.Reflected!.ParmsSize]);
        return 1;
    }

    private sealed record HudTargets(nint GameMode, nint Broker, nint[] Widgets, GameFunction? SetMax, GameFunction? SetScore, GameFunction? OnScore);

    private sealed record HudUpdate(nint GameMode, int[] Lives, int[] Players, int MaxLives, long Sequence);

    /// <summary>Whether a HUD function starts with (int32 player, int32 score), the layout the push writes.</summary>
    private static bool TakesPlayerAndScore(GameFunction? function)
    {
        if (function?.Signature.Reflected is not { } sig || sig.ParmsSize > 16)
        {
            return false;
        }

        var ints = sig.Parameters.Where(p => !p.IsReturn).Take(2).ToArray();
        return ints.Length == 2 && ints.All(p => p.Type == "IntProperty") && ints[0].Offset == 0 && ints[1].Offset == 4;
    }

    private static bool IsFighter(ObjectFinder finder, nint pawn)
    {
        nint fighterClass = finder.FindClass("MvsFixedCharacter");
        return fighterClass != 0 && ObjectHeader.TryRead(finder.Memory, pawn, out var header) && ObjectFinder.Inherits(finder.Memory, header.ClassPrivate, fighterClass);
    }

    private static bool TryReadInt(nint address, out int value) => CodeWriter.TryRead(address, out value);

    // For fields where an unreadable value only means the fighter is not tracked or the rules stay off.
    private static int ReadInt(nint address) => TryReadInt(address, out int value) ? value : -1;
}
