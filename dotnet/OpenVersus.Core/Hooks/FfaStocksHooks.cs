using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// FFA stock rules in the game (<see cref="FfaStocks"/>): in a match whose mode is "FFA", every
/// fighter gets the server's ringout count as lives, a fighter out of lives is not respawned,
/// and the last one standing wins. Entry hooks on four native functions of this build; the C++
/// client's StockDiagnostics, without its diagnostics.
/// <list type="bullet">
/// <item>AMvsGameModeBase::RegisterCharacter: a new game mode starts a match (the mode and lives
/// come from UMvsGameplayConfig); each fighter is recorded.</item>
/// <item>AMvsGameModeBase::PlayerDied: on a fighter's first death RespawnsRemaining (-1, unlimited)
/// becomes lives - 1; a death at 0 puts it out; when one fighter is left, EndMatch(its team).</item>
/// <item>APfgFixedPawn::DoRespawn: skipped for a fighter at 0, which the native code would revive.</item>
/// <item>UMvsGameModeDefaultGameEndHandlerComponent::AttemptEndMatch: the game's score-based end is
/// skipped until these rules end the match.</item>
/// </list>
/// Every peer runs the same rules on the same simulated state, so they agree. The hooks run on the
/// game thread; the HUD update is posted to it to run after the simulation step.
/// </summary>
public static unsafe class FfaStocksHooks
{
    // RVAs and prologues from a disassembly of the final build (MultiVersus-Win64-Shipping.exe,
    // 2026-09-27); the RVAs are the C++ client's (StockDiagnostics.cpp).
    private const uint PlayerDiedRva = 0x02966870;
    private const uint RegisterCharacterRva = 0x02966E20;
    private const uint DoRespawnRva = 0x011E4F70;
    private const uint AttemptEndMatchRva = 0x02967BE0;
    private const uint SetRespawnsRemainingRva = 0x011FC980;

    // test rdx, rdx; je rel32
    private static readonly byte[] s_playerDiedPrologue = [0x48, 0x85, 0xD2, 0x0F, 0x84, 0x9A, 0x05, 0x00, 0x00];
    // push rbx; push rsi; push rdi; sub rsp, 0xC0
    private static readonly byte[] s_registerCharacterPrologue = [0x40, 0x53, 0x56, 0x57, 0x48, 0x81, 0xEC, 0xC0, 0x00, 0x00, 0x00];
    // mov eax, [rcx+0x380]
    private static readonly byte[] s_doRespawnPrologue = [0x8B, 0x81, 0x80, 0x03, 0x00, 0x00];
    // push rsi; sub rsp, 0x50
    private static readonly byte[] s_attemptEndMatchPrologue = [0x40, 0x56, 0x48, 0x83, 0xEC, 0x50];
    // mov [rcx+0x380], edx; ret
    private static readonly byte[] s_setRespawnsRemainingCode = [0x89, 0x91, 0x80, 0x03, 0x00, 0x00, 0xC3];

    private static delegate* unmanaged<nint, nint, nint, void> s_playerDied;
    private static delegate* unmanaged<nint, nint, void> s_registerCharacter;
    private static delegate* unmanaged<nint, void> s_doRespawn;
    private static delegate* unmanaged<nint, void> s_attemptEndMatch;
    private static delegate* unmanaged<nint, int, void> s_setRespawnsRemaining;

    private static ILogger? s_log;
    private static ObjectFinder? s_finder;
    private static nint s_gameMode;
    private static FfaStocks? s_match;
    private static bool s_endBlockLogged;
    private static HudTargets? s_hud;
    private static long s_hudSequence;
    private static long s_hudApplied;

    /// <summary>
    /// Hooks the four functions. False, or a <see cref="PatchException"/>, when this build's code
    /// is not what the disassembly found; nothing is hooked unless every prologue matches.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        c.Log.Info("==FFA Stocks==");
        var image = c.Image;
        nint setRespawns = image.Address(SetRespawnsRemainingRva);
        CodeWriter.Expect(setRespawns, s_setRespawnsRemainingCode);
        foreach (var (rva, prologue) in new[] { (PlayerDiedRva, s_playerDiedPrologue), (RegisterCharacterRva, s_registerCharacterPrologue), (DoRespawnRva, s_doRespawnPrologue), (AttemptEndMatchRva, s_attemptEndMatchPrologue) })
        {
            CodeWriter.Expect(image.Address(rva), prologue);
        }

        s_setRespawnsRemaining = (delegate* unmanaged<nint, int, void>)setRespawns;
        GameFunctions.FromRva("APfgFixedPawn::SetRespawnsRemaining", SetRespawnsRemainingRva, "void APfgFixedPawn::SetRespawnsRemaining(APfgFixedPawn* this, int32 limit)", image);

        s_playerDied = (delegate* unmanaged<nint, nint, nint, void>)Hook(image, "AMvsGameModeBase::PlayerDied", PlayerDiedRva, s_playerDiedPrologue, (nint)(delegate* unmanaged<nint, nint, nint, void>)&PlayerDied, "void AMvsGameModeBase::PlayerDied(AMvsGameModeBase* this, AMvsFixedCharacter* victim, AMvsFixedCharacter* attacker)", endsInConditionalJump: true);
        s_registerCharacter = (delegate* unmanaged<nint, nint, void>)Hook(image, "AMvsGameModeBase::RegisterCharacter", RegisterCharacterRva, s_registerCharacterPrologue, (nint)(delegate* unmanaged<nint, nint, void>)&RegisterCharacter, "void AMvsGameModeBase::RegisterCharacter(AMvsGameModeBase* this, APfgFixedPawn* character)");
        s_doRespawn = (delegate* unmanaged<nint, void>)Hook(image, "APfgFixedPawn::DoRespawn", DoRespawnRva, s_doRespawnPrologue, (nint)(delegate* unmanaged<nint, void>)&DoRespawn, "void APfgFixedPawn::DoRespawn(APfgFixedPawn* this)");
        s_attemptEndMatch = (delegate* unmanaged<nint, void>)Hook(image, "UMvsGameModeDefaultGameEndHandlerComponent::AttemptEndMatch", AttemptEndMatchRva, s_attemptEndMatchPrologue, (nint)(delegate* unmanaged<nint, void>)&AttemptEndMatch, "void UMvsGameModeDefaultGameEndHandlerComponent::AttemptEndMatch(UMvsGameModeDefaultGameEndHandlerComponent* this)");

        c.Log.Success("FFA stock rules hooked");
        return true;
    }

    /// <summary>The object finder the rules read the gameplay config and HUD through; until it is attached they stay off.</summary>
    public static void Attach(ObjectFinder finder) => s_finder = finder;

    private static nint Hook(GameImage image, string name, uint rva, byte[] prologue, nint hook, string declaration, bool endsInConditionalJump = false)
    {
        nint gateway = EntryHook.Install(image.Address(rva), prologue, hook, endsInConditionalJump);
        GameFunctions.Register(name, gateway, FunctionSource.Rva, $"rva 0x{rva:X}, entry hooked; this is the gateway to the original", declaration, image);
        return gateway;
    }

    [UnmanagedCallersOnly]
    private static void RegisterCharacter(nint gameMode, nint character)
    {
        s_registerCharacter(gameMode, character);
        HookGuard.Run("FfaRegisterCharacter", (gameMode, character), static s => OnRegisterCharacter(s.gameMode, s.character));
    }

    [UnmanagedCallersOnly]
    private static void PlayerDied(nint gameMode, nint victim, nint attacker)
    {
        int respawns = HookGuard.Run("FfaPlayerDied", victim, static v => BeforeDeath(v), int.MinValue);
        s_playerDied(gameMode, victim, attacker);
        if (respawns != int.MinValue)
        {
            HookGuard.Run("FfaPlayerDied", (gameMode, victim, respawns), static s => AfterDeath(s.gameMode, s.victim, s.respawns));
        }
    }

    [UnmanagedCallersOnly]
    private static void DoRespawn(nint pawn)
    {
        bool skip = HookGuard.Run("FfaDoRespawn", pawn, static p => s_match is { } match && TryReadInt(p + Mvs.PawnRespawnsRemaining, out int respawns) && match.SkipsRespawn(p, respawns), false);
        if (!skip)
        {
            s_doRespawn(pawn);
        }
    }

    [UnmanagedCallersOnly]
    private static void AttemptEndMatch(nint component)
    {
        bool block = HookGuard.Run("FfaAttemptEndMatch", component, static _ => s_match is { Ended: false }, false);
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
    }

    /// <summary>
    /// A new game mode is a new match: read its mode and ringouts, and turn the rules on for FFA.
    /// Then record the character if it is a fighter.
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
            s_match = StartMatch(finder);
            s_endBlockLogged = false;
        }

        if (s_match is not { } match || !IsFighter(finder, character))
        {
            return;
        }

        nint data = character + Mvs.FixedCharacterGameplayPlayerData;
        match.Register(character, ReadInt(data + Mvs.GameplayPlayerDataPlayerIndex), ReadInt(data + Mvs.GameplayPlayerDataTeamIndex));
    }

    private static FfaStocks? StartMatch(ObjectFinder finder)
    {
        nint config = finder.FindInstanceOfClass(finder.FindClass("MvsGameplayConfig"));
        if (config == 0 || !GameStrings.TryReadFString(finder.Memory, config + Mvs.GameplayConfigModeString, out string mode))
        {
            s_log?.Warn("[FFA] gameplay config unreadable; stock rules off for this match");
            return null;
        }

        if (!FfaStocks.IsFfa(mode))
        {
            return null;
        }

        int ringouts = ReadInt(config + Mvs.GameplayConfigNumRingouts);
        var match = new FfaStocks(FfaStocks.LivesFromRingouts(ringouts));
        // Resolved now, not at the final death, so the object scan happens while the match loads.
        if (Reflection.Find(finder, finder.Image, "MvsGameModeBase", "EndMatch") == null)
        {
            s_log?.Warn("[FFA] AMvsGameModeBase::EndMatch not found; stock rules off for this match");
            return null;
        }

        s_log?.Info($"[FFA] stock rules on: {match.Lives} lives each (server ringouts {ringouts})");
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
        if (respawns == 0)
        {
            s_log?.Info($"[FFA] fighter out ({match.EliminatedCount}/{match.FighterCount})");
        }

        PostLivesToHud(match);
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
    private static void PostLivesToHud(FfaStocks match)
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
