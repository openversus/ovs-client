using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using OpenVersus.Native;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Friendly fire, the OVS mutator <see cref="Slug"/>: in a match whose config carries it, a
/// fighter's hits land on their teammate as they would on an opponent. Only the server turns it
/// on, by selecting the mutator.
/// <para>
/// The rules live in the experimental pak (OVS_Experimental_P): its buff, MvsBuff_OVS_FriendlyFire,
/// is on every fighter and answers the defender's OverrideResponseFlags (which GetHitResponseFlags
/// asks first), so a teammate's hit takes the enemy path, except the ally interactions it keeps
/// (support moves, held and attached objects, a fighter's own summons...). It also halves a teammate's
/// damage, gives a teammate the hit's enemy on-hit debuffs (stuns, burns) and stops a teammate's
/// hit refilling the dodge meter. What a Blueprint cannot reach is here:
/// </para>
/// <list type="bullet">
/// <item>The team check in the shield code, one of the 18 calls to UMvsTeamComponent::IsSameTeam: it
/// asks the pak's rule (the buff's OVS_OverrideFlags) and, for a teammate's hit the pak lets hurt,
/// says the two are not on the same team, so a shield blocks it and armor takes it as an opponent's.
/// A kept hit (Wonder Woman's armor) stays a teammate's there.</item>
/// <item>The hit resolution's GetHitResponseFlags call: for a teammate's hit the pak turned into an
/// enemy hit and its OVS_SkipsGains names (attackers whose successful hit gains them something: Finn's
/// coins, LeBron's ball), the hit's HitSkipsSuccessfulAttack is set, so the game skips its
/// successful-attack events; other teammate hits keep them, so on-hit debuffs still land.</item>
/// <item>PandaGameState_C's friendly-fire flag is set, and the friendly-fire parameter of its
/// PandaGameStateMatchStarted event; both are checked against the class's reflection first.</item>
/// </list>
/// A defender without the pak's buff gets no friendly fire: teammates stay teammates. Every peer
/// applies the same rules to the same simulated hit, so they agree.
/// </summary>
public static unsafe class FriendlyFireHooks
{
    /// <summary>The mutator's slug.</summary>
    public const string Slug = "ovs_friendly_fire";

    // The patterns live here, not in OpenVersus.toml: the mutator is the server's to turn on, not a player's.
    // Whole instructions from the game's only build, with rel32 targets and RIP-relative displacements as
    // wildcards; each matches once there (FriendlyFireHooksTests has the bytes).
    /// <summary>UMvsDefenderComponent::GetHitResponseFlags's start (0x02957C10).</summary>
    internal const string GetHitResponseFlagsPattern = "48 89 5C 24 10 48 89 6C 24 18 57 41 54 41 55 41 56 41 57 48 81 EC E0 0A 00 00";
    /// <summary>UMvsTeamComponent::IsSameTeam's start (0x0121BFB0).</summary>
    internal const string IsSameTeamPattern = "48 85 D2 74 1F 80 7A 30 00 75 19 F7 42 08 00 00 00 60 75 10 8B 89 D0 00 00 00";
    /// <summary>The shield code's team check (0x02953875), ending in its call to IsSameTeam, whose return address the C++ matched (0x0295388C).</summary>
    internal const string ShieldTeamCheckPattern = "80 79 30 00 75 19 F7 41 08 00 00 00 60 75 10 49 8B D6 E8 ? ? ? ?";
    /// <summary>Where the call to IsSameTeam is, from the start of <see cref="ShieldTeamCheckPattern"/>.</summary>
    internal const int ShieldTeamCheckCall = 18;
    /// <summary>
    /// The hit resolution's call to GetHitResponseFlags (0x026155F8): lea r9, [rsi+0x37C] (the hit's ResponseFlags, passed
    /// as the out pointer); movzx r8d, bl; mov rdx, rax; mov rcx, rdi; call. The hit it passes in rdx is a copy, so the
    /// real hit is reached through the flags pointer.
    /// </summary>
    internal const string ResolveResponseCallPattern = "4C 8D 8E 7C 03 00 00 44 0F B6 C3 48 8B D0 48 8B CF E8 ? ? ? ?";
    /// <summary>Where the call is, from the start of <see cref="ResolveResponseCallPattern"/>.</summary>
    internal const int ResolveResponseCall = 17;
    /// <summary>FHitResolutionData.HitSkipsSuccessfulAttack (hit +0x379) from ResponseFlags (hit +0x37C).</summary>
    private const int SkipsSuccessfulAttackFromFlags = 0x379 - 0x37C;

    // push rbp; push rsi; push rdi; push r12
    private static readonly byte[] s_processEventPrologue = [0x40, 0x55, 0x56, 0x57, 0x41, 0x54];

    // The pak's friendly-fire buff, whose Blueprint rules are called through ProcessEvent, and the hit's size.
    private const string PakBuffClass = "MvsBuff_OVS_FriendlyFire_C";
    private const int ActiveHitInteractionSize = 0x390;

    /// <summary>The most lines one match writes to <see cref="MatchRulesLog"/>; a repeat of the line before counts once.</summary>
    private const int MaxLoggedPerMatch = 2000;

    private static delegate* unmanaged<nint, nint, byte> s_isSameTeam;
    private static delegate* unmanaged<nint, nint, byte, int*, void> s_resolveResponse;
    private static delegate* unmanaged<nint, nint, nint, void> s_processEvent;
    private static nint s_watchSlot;

    private static ILogger? s_log;
    private static ObjectFinder? s_finder;
    private static bool s_installed;
    private static bool s_offlineTesting;
    private static volatile bool s_active;
    private static nint s_fighterClass;
    private static FName s_pakBuff;
    private static readonly PakRule s_overrideFlags = new("OVS_OverrideFlags", "ReturnValue", ally: true);
    private static readonly PakRule s_skipsGains = new("OVS_SkipsGains", "Skip", ally: false);
    private static nint s_gameState;
    private static bool s_gameStateFlagOk;
    private static bool s_matchStartedParamOk;
    private static int s_logged;
    // Class pointer -> whether it is a fighter class, so the class chain is walked once per class.
    private static readonly Dictionary<nint, bool> s_fighterClasses = [];
    private static string? s_lastLine;
    private static int s_repeats;
    private static readonly Dictionary<nint, string> s_classNames = [];

    /// <summary>
    /// Points the shield's team check and the hit resolution's response-flags call at this class, and hooks ProcessEvent
    /// through a filter for the match-start event. Nothing changes until <see cref="StartMatch"/> sees the mutator.
    /// False, with nothing hooked, when a pattern is missing; throws a <see cref="PatchException"/> when this build's code
    /// is not what the disassembly found. The ProcessEvent hook alone may fail (another mod on it), which only loses the
    /// match-start parameter.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        s_offlineTesting = c.Settings.FriendlyFireOffline;
        c.Log.Info("==Friendly Fire==");
        var image = c.Image;
        var hitResponse = c.Patterns.Find("FriendlyFireResponse", GetHitResponseFlagsPattern);
        var isSameTeam = c.Patterns.Find("FriendlyFireIsSameTeam", IsSameTeamPattern);
        var shield = c.Patterns.Find("FriendlyFireShield", ShieldTeamCheckPattern);
        var resolve = c.Patterns.Find("FriendlyFireResolveResponse", ResolveResponseCallPattern);
        if (!hitResponse.Found || !isSameTeam.Found || !shield.Found || !resolve.Found)
        {
            c.Log.Error("Friendly fire: a function it needs was not found; nothing is hooked and the Friendly Fire mutator will not work");
            return false;
        }

        nint shieldCall = shield.Address + ShieldTeamCheckCall;
        if (CallSite.Destination(shieldCall) != isSameTeam.Address)
        {
            throw new PatchException($"the shield's team check at 0x{shieldCall:X} does not call IsSameTeam at 0x{isSameTeam.Address:X}");
        }

        nint resolveCall = resolve.Address + ResolveResponseCall;
        if (CallSite.Destination(resolveCall) != hitResponse.Address)
        {
            throw new PatchException($"the hit resolution's call at 0x{resolveCall:X} does not call GetHitResponseFlags at 0x{hitResponse.Address:X}");
        }

        s_isSameTeam = (delegate* unmanaged<nint, nint, byte>)isSameTeam.Address;
        GameFunctions.Register("UMvsTeamComponent::IsSameTeam", isSameTeam, FunctionSource.Pattern, "FriendlyFireIsSameTeam", "bool UMvsTeamComponent::IsSameTeam(UMvsTeamComponent* this, UMvsTeamComponent* other)", image);
        GameFunctions.Register("UMvsDefenderComponent::GetHitResponseFlags", hitResponse, FunctionSource.Pattern, "FriendlyFireResponse", "void UMvsDefenderComponent::GetHitResponseFlags(UMvsDefenderComponent* this, const FActiveHitInteraction* hit, bool isAlly, int32* flags)", image);
        RedirectShieldCheckWithHit(shieldCall);
        s_resolveResponse = (delegate* unmanaged<nint, nint, byte, int*, void>)CallSite.Redirect(resolveCall,
            (nint)(delegate* unmanaged<nint, nint, byte, int*, void>)&ResolveResponse);

        try
        {
            s_processEvent = (delegate* unmanaged<nint, nint, nint, void>)EntryHook.InstallFiltered(image.Address(GameFunctions.ProcessEventRva), s_processEventPrologue,
                (nint)(delegate* unmanaged<nint, nint, nint, void>)&MatchStarted, out s_watchSlot);
        }
        catch (PatchException e)
        {
            c.Log.Warn($"Friendly fire: ProcessEvent could not be hooked ({e.Message}); the match-start parameter will not be set");
        }

        s_installed = true;
        c.Log.Success($"Friendly fire hooked (shield check at 0x{shieldCall:X}, hit resolution at 0x{resolveCall:X}); it is on only in matches with the Friendly Fire mutator");
        return true;
    }

    /// <summary>
    /// Points the shield's IsSameTeam call at <see cref="ShieldIsSameTeam"/> through a stub that also passes the hit
    /// being checked. The shield code (in the function at 0x02953020, which takes the FActiveHitInteraction as its
    /// second argument and keeps it in r15: mov r15, rdx; it reads [r15+0x330], the defender, just before) runs before
    /// the hit's response flags are asked for, so the hit is not otherwise known yet. The stub's prelude is mov r8, r15.
    /// </summary>
    private static void RedirectShieldCheckWithHit(nint shieldCall) =>
        CallSite.InjectWithPrelude(shieldCall, s_passHitInR8, (nint)(delegate* unmanaged<nint, nint, nint, byte>)&ShieldIsSameTeam, jump: false);

    // mov r8, r15
    private static readonly byte[] s_passHitInR8 = [0x4D, 0x89, 0xF8];

    /// <summary>
    /// A new match (<see cref="StockRulesHooks"/>, on the game thread): friendly fire is on when the
    /// server selected the mutator, off otherwise, including when the settings could not be read.
    /// </summary>
    public static void StartMatch(ObjectFinder finder, nint gameMode, MatchSettings? settings)
    {
        s_active = false;
        s_logged = 0;
        s_fighterClasses.Clear();
        s_lastLine = null;
        s_repeats = 0;
        s_gameState = 0;
        s_gameStateFlagOk = false;
        s_matchStartedParamOk = false;
        if (!s_installed)
        {
            return;
        }

        SetWatched(0);
        if (settings == null || !IsOn(settings, s_offlineTesting))
        {
            MatchRulesLog.Line("friendly fire: off");
            return;
        }

        s_finder = finder;
        s_fighterClass = finder.FindClass("MvsFixedCharacter");
        s_pakBuff = finder.Names.Find(PakBuffClass);
        if (s_fighterClass == 0)
        {
            s_log?.Warn("[FF] the fighter class was not found; friendly fire off for this match");
            return;
        }

        CheckGameStateFlag(finder, gameMode);
        CheckMatchStarted(finder);
        s_active = true;
        EnsureGameStateFlag();
        s_log?.Info($"[FF] friendly fire on ({(settings.HasWorldBuff(Slug) ? "mutator" : "offline testing")})");
        MatchRulesLog.Line($"friendly fire: ON (the pak's buff {PakBuffClass} {(s_pakBuff.Index != 0 ? "is loaded" : "is not loaded yet")})");
    }

    /// <summary>
    /// Whether friendly fire is on for a match: the server selected the mutator, or, with
    /// <paramref name="offlineTesting"/> ([Settings.Debug] FriendlyFireOffline), the match is offline
    /// Local Play or the Lab. An online match never gets it from the setting, so every player in one
    /// always runs the same rules.
    /// </summary>
    public static bool IsOn(MatchSettings settings, bool offlineTesting) =>
        settings.HasWorldBuff(Slug) || (offlineTesting && !settings.Online && settings.MatchType is MatchSettings.LocalPlayMatchType or MatchSettings.LabMatchType);

    /// <summary>Finds the game state through the game mode and checks that its field at the flag's offset is a plain bool.</summary>
    private static void CheckGameStateFlag(ObjectFinder finder, nint gameMode)
    {
        s_gameStateFlagOk = false;
        if (!CodeWriter.TryRead(gameMode + Mvs.GameModeGameState, out nint gameState) || gameState == 0
            || !ObjectHeader.TryRead(finder.Memory, gameState, out var header))
        {
            MatchRulesLog.Line("friendly fire: no game state on the game mode yet");
            return;
        }

        var property = FindProperty(finder, header.ClassPrivate, Mvs.PandaGameStateFriendlyFire);
        s_gameStateFlagOk = property is { Type: "BoolProperty", FieldMask: 0xFF };
        s_gameState = gameState;
        MatchRulesLog.Line($"friendly fire: game state {ClassName(gameState)} +0x{Mvs.PandaGameStateFriendlyFire:X} is {property?.ToString() ?? "no property"}; {(s_gameStateFlagOk ? "will set it" : "left alone")}");
    }

    /// <summary>Watches PandaGameStateMatchStarted when its parameter at the friendly-fire offset is a bool.</summary>
    private static void CheckMatchStarted(ObjectFinder finder)
    {
        s_matchStartedParamOk = false;
        if (s_watchSlot == 0)
        {
            return;
        }

        var function = Reflection.Find(finder, finder.Image, "PandaGameState_C", "PandaGameStateMatchStarted");
        var parameter = function?.Signature.Reflected?.Parameters.FirstOrDefault(p => p.Offset == Mvs.MatchStartedFriendlyFireParam && !p.IsReturn);
        s_matchStartedParamOk = parameter is { Type: "BoolProperty" };
        MatchRulesLog.Line($"friendly fire: PandaGameStateMatchStarted {(function == null ? "not found" : $"parameter at +{Mvs.MatchStartedFriendlyFireParam} is {parameter?.ToString() ?? "none"}")}; {(s_matchStartedParamOk ? "will set it" : "left alone")}");
        if (s_matchStartedParamOk)
        {
            SetWatched(function!.Address);
        }
    }

    private static void SetWatched(nint function)
    {
        if (s_watchSlot != 0)
        {
            EntryHook.SetWatched(s_watchSlot, function);
        }
    }

    /// <summary>
    /// The shield's team check: for a teammate's hit while friendly fire is on, asks the pak's rule (the defender's buff's
    /// OVS_OverrideFlags, which answers true, with the enemy flags, for a hit that hurts). One it lets hurt makes the two
    /// opponents here, so the shield and armor treat it as an opponent's hit; one it keeps (Wonder Woman's armor, a
    /// support move) leaves them teammates. No pak buff on the defender: teammates.
    /// </summary>
    [UnmanagedCallersOnly]
    private static byte ShieldIsSameTeam(nint team, nint other, nint hit)
    {
        byte same = s_isSameTeam(team, other);
        if (same == 0 || !s_active || team == other || hit == 0)
        {
            return same;
        }

        bool hurts = HookGuard.Run("FriendlyFireShield", hit, static h => AskPak(s_overrideFlags, h), false);
        if (MatchRulesLog.On)
        {
            LogLine(hurts ? "ff shield check: the pak lets a teammate's hit hurt; teammates count as opponents" : "ff shield check: teammates stay teammates");
        }

        return hurts ? (byte)0 : same;
    }

    /// <summary>
    /// The hit resolution's GetHitResponseFlags call: the game's own (the pak's OVS_OverrideFlags answers inside it), then,
    /// for a teammate's hit that now takes the enemy path (flags other than 0 and the ally flags 0x20) and that the pak's
    /// OVS_SkipsGains names, the hit's HitSkipsSuccessfulAttack is set, so the game skips its successful-attack events for
    /// it (exe 8f7b2ca3: 0x142952E5B and 0x142952F31 check it). The only other reader, LastHitAbilityId (0x1429547A0), feeds
    /// the dodge refund the pak already takes from teammate hits.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void ResolveResponse(nint defender, nint hit, byte isAlly, int* flags)
    {
        s_resolveResponse(defender, hit, isAlly, flags);
        if (!s_active || isAlly == 0 || flags == null)
        {
            return;
        }

        HookGuard.Run("FriendlyFireSkipGains", (hit, f: (nint)flags), static s =>
        {
            EnsureGameStateFlag();
            if (CodeWriter.TryRead(s.f, out int value) && value != 0 && value != 0x20 && AskPak(s_skipsGains, s.hit)
                && CodeWriter.TryWrite(s.f + SkipsSuccessfulAttackFromFlags, (byte)1) && MatchRulesLog.On)
            {
                LogLine($"ff skip: a teammate's hit (flags 0x{value:X}) skips the successful-attack events (the pak's gain list)");
            }
        });
    }

    /// <summary>One of the pak buff's Blueprint rules, resolved once: (AttackInfo[, IsAllyInteraction]) -> a bool.</summary>
    private sealed class PakRule(string function, string result, bool ally)
    {
        public string Function { get; } = function;
        public string Result { get; } = result;
        public bool Ally { get; } = ally;
        public GameFunction? Resolved { get; set; }
        public byte[] Parms { get; set; } = [];
    }

    /// <summary>
    /// Calls <paramref name="rule"/> on the defender's friendly-fire buff with a copy of the hit (and IsAllyInteraction
    /// set when the rule takes it), through ProcessEvent, the way the game's delegate calls it; its bool result. False
    /// when the defender has no such buff or the pak has no such function.
    /// </summary>
    private static bool AskPak(PakRule rule, nint hit)
    {
        if (s_finder is not { } finder)
        {
            return false;
        }

        if (s_pakBuff.Index == 0)
        {
            s_pakBuff = finder.Names.Find(PakBuffClass);
        }

        nint fighter = Read(Read(hit, Mvs.HitInteractionDefender), Mvs.ActorComponentOwner);
        nint buff = !IsFighterObject(fighter) || s_pakBuff.Index == 0 ? 0
            : ReadArray(Read(Read(fighter, PawnComponentCache), ComponentCacheBuff), BuffActiveBuffs, 16).FirstOrDefault(b => ClassIs(b, s_pakBuff));
        if (buff == 0)
        {
            return false;
        }

        rule.Resolved ??= Reflection.Find(finder, finder.Image, PakBuffClass, rule.Function);
        var sig = rule.Resolved?.Signature.Reflected;
        var info = sig?.Parameters.FirstOrDefault(x => x.Name == "AttackInfo");
        var result = sig?.Parameters.FirstOrDefault(x => x.Name == rule.Result);
        var isAlly = sig?.Parameters.FirstOrDefault(x => x.Name == "IsAllyInteraction");
        if (sig == null || info == null || result == null || info.Size != ActiveHitInteractionSize || (rule.Ally && isAlly == null))
        {
            return false;
        }

        if (rule.Parms.Length != sig.ParmsSize)
        {
            rule.Parms = new byte[sig.ParmsSize];
        }

        Span<byte> parms = rule.Parms;
        parms.Clear();
        if (!CodeWriter.TryRead(hit, parms.Slice(info.Offset, ActiveHitInteractionSize)))
        {
            return false;
        }

        if (rule.Ally)
        {
            parms[isAlly!.Offset] = 1;
        }

        Reflection.Invoke(rule.Resolved!, buff, parms);
        return parms[result.Offset] != 0;
    }

    [UnmanagedCallersOnly]
    private static void MatchStarted(nint gameState, nint function, nint parameters)
    {
        HookGuard.Run("FriendlyFireMatchStarted", parameters, static p => ForceMatchStartedParameter(p));
        s_processEvent(gameState, function, parameters);
        HookGuard.Run("FriendlyFireMatchStarted", gameState, static g =>
        {
            if (s_active && g != 0)
            {
                s_gameState = g;
                EnsureGameStateFlag();
            }
        });
    }

    private static void ForceMatchStartedParameter(nint parameters)
    {
        if (!s_active || !s_matchStartedParamOk || parameters == 0)
        {
            return;
        }

        nint friendlyFire = parameters + Mvs.MatchStartedFriendlyFireParam;
        if (!CodeWriter.TryRead(friendlyFire, out byte was) || !CodeWriter.TryWrite(friendlyFire, (byte)1))
        {
            s_log?.Warn($"[FF] match start: the friendly fire parameter at 0x{friendlyFire:X} could not be set");
            return;
        }

        s_log?.Info($"[FF] match start: friendly fire parameter {was} -> 1");
        MatchRulesLog.Line($"friendly fire: PandaGameStateMatchStarted parameter {was} -> 1");
    }

    /// <summary>Sets the game state's friendly-fire flag when it is a checked bool and not already set; the C++ kept it set the same way.</summary>
    private static void EnsureGameStateFlag()
    {
        if (!s_gameStateFlagOk || s_gameState == 0)
        {
            return;
        }

        nint flag = s_gameState + Mvs.PandaGameStateFriendlyFire;
        if (CodeWriter.TryRead(flag, out byte was) && was != 1 && CodeWriter.TryWrite(flag, (byte)1))
        {
            MatchRulesLog.Line($"friendly fire: game state flag {was} -> 1");
        }
    }

    private static bool IsFighterObject(nint obj)
    {
        if (obj == 0 || s_finder is not { } finder || !CodeWriter.TryRead(obj + Mvs.ObjectClassPrivate, out nint cls) || cls == 0)
        {
            return false;
        }

        if (!s_fighterClasses.TryGetValue(cls, out bool fighter))
        {
            fighter = ObjectFinder.Inherits(finder.Memory, cls, s_fighterClass);
            s_fighterClasses[cls] = fighter;
        }

        return fighter;
    }

    // APfgFixedPawn.ComponentCache, UMvsComponentCache.BuffComponent, UMvsBuffComponent.ActiveBuffs (CXXHeaderDump).
    private const int PawnComponentCache = 0x388;
    private const int ComponentCacheBuff = 0x138;
    private const int BuffActiveBuffs = 0xF8;

    private static bool ClassIs(nint obj, FName expected) =>
        obj != 0 && CodeWriter.TryRead(obj + Mvs.ObjectClassPrivate, out nint cls) && CodeWriter.TryRead(cls + Mvs.ObjectNamePrivate, out FName name) && Is(name, expected);

    private static bool Is(FName name, FName expected) => expected.Index != 0 && name.Index == expected.Index && name.Number == expected.Number;

    private static nint Read(nint obj, int offset) => obj != 0 && CodeWriter.TryRead(obj + offset, out nint value) ? value : 0;

    /// <summary>The pointers of a TArray at <paramref name="obj"/> + <paramref name="offset"/>, at most <paramref name="max"/>.</summary>
    private static List<nint> ReadArray(nint obj, int offset, int max)
    {
        var items = new List<nint>();
        nint data = Read(obj, offset);
        if (data != 0 && CodeWriter.TryRead(obj + offset + 8, out int count))
        {
            for (int i = 0; i < Math.Min(count, max); i++)
            {
                items.Add(Read(data, i * nint.Size));
            }
        }

        return items;
    }

    /// <summary>
    /// Writes <paramref name="line"/> unless it repeats the one before; the repeats are counted onto the next different
    /// line. At most <see cref="MaxLoggedPerMatch"/> lines.
    /// </summary>
    private static void LogLine(string line)
    {
        if (line == s_lastLine)
        {
            s_repeats++;
            return;
        }

        if (s_logged >= MaxLoggedPerMatch)
        {
            return;
        }

        if (s_repeats > 0)
        {
            MatchRulesLog.Line($"  (the line above {s_repeats} more times)");
        }

        s_logged++;
        s_lastLine = line;
        s_repeats = 0;
        MatchRulesLog.Line(line);
    }

    private static string ClassName(nint obj)
    {
        if (obj == 0)
        {
            return "-";
        }

        if (!CodeWriter.TryRead(obj + Mvs.ObjectClassPrivate, out nint cls) || cls == 0)
        {
            return "?";
        }

        if (!s_classNames.TryGetValue(cls, out string? name))
        {
            name = ObjectName(cls);
            s_classNames[cls] = name;
        }

        return name;
    }

    private static string ObjectName(nint obj) =>
        obj == 0 ? "-" : CodeWriter.TryRead(obj + Mvs.ObjectNamePrivate, out FName name) ? s_finder?.Names.ToString(name) ?? $"#{name.Index}" : "?";

    /// <summary>The property of <paramref name="uclass"/> (or a parent) at <paramref name="offset"/>.</summary>
    private static ClassProperty? FindProperty(ObjectFinder finder, nint uclass, int offset)
    {
        for (int depth = 0; uclass != 0 && depth < 32; depth++)
        {
            finder.Memory.TryRead(uclass + Reflection.UStructChildProperties, out nint field);
            for (int guard = 0; field != 0 && guard < 512; guard++)
            {
                if (finder.Memory.TryRead(field + Reflection.FPropertyOffset, out int at) && at == offset)
                {
                    finder.Memory.TryRead(field + Reflection.FFieldNamePrivate, out FName name);
                    finder.Memory.TryRead(field + Reflection.FFieldClassPrivate, out nint fieldClass);
                    finder.Memory.TryRead(fieldClass, out FName type);
                    finder.Memory.TryRead(field + FBoolPropertyFieldMask, out byte mask);
                    string typeName = finder.Names.ToString(type) ?? "?";
                    return new ClassProperty(finder.Names.ToString(name) ?? $"#{name.Index}", typeName, typeName == "BoolProperty" ? mask : (byte)0);
                }

                if (!finder.Memory.TryRead(field + Reflection.FFieldNext, out field))
                {
                    break;
                }
            }

            finder.Memory.TryRead(uclass + Mvs.StructSuperStruct, out uclass);
        }

        return null;
    }

    /// <summary>FBoolProperty::FieldMask (UE 5.1: FieldSize, ByteOffset, ByteMask, FieldMask after the 0x78-byte FProperty); 0xFF for a plain bool.</summary>
    private const int FBoolPropertyFieldMask = 0x7B;

    private sealed record ClassProperty(string Name, string Type, byte FieldMask)
    {
        public override string ToString() => $"{Type} {Name}{(Type == "BoolProperty" ? $" (mask 0x{FieldMask:X2})" : "")}";
    }
}
