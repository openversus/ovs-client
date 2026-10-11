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
/// on, by selecting the mutator. The C++ client's friendly fire (StockDiagnostics.cpp), ported:
/// <list type="bullet">
/// <item>ProcessActiveHitInteraction, which every hit goes through before the attacker and the
/// defender hear of it: IsAllyInteraction is cleared, so the hit takes the enemy path.</item>
/// <item>UMvsDefenderComponent::GetHitResponseFlags: asked for the ally response flags, it is asked
/// for the enemy ones instead, in its one call (calling it twice runs Blueprint overrides twice).</item>
/// <item>The team check in the shield code, one of the 18 calls to UMvsTeamComponent::IsSameTeam:
/// two teammates are not on the same team there, so a shield blocks a teammate's hit.</item>
/// <item>PandaGameState_C's friendly-fire flag is set, and the friendly-fire parameter of its
/// PandaGameStateMatchStarted event; both are checked against the class's reflection first.</item>
/// </list>
/// Some ally interactions keep the ally path: a fighter's own objects and what it holds (thrown
/// items, caught projectiles, a carried helper), hits on anything that is not a fighter, Raven's
/// link pulse, Velma's books (the game decides from her marks), Taz's dogpile with his perk (which
/// a teammate joins), and Jerry's cork while Jerry is carried. Every peer applies the same rule to
/// the same simulated hit, so they agree.
/// </summary>
public static unsafe class FriendlyFireHooks
{
    /// <summary>The mutator's slug.</summary>
    public const string Slug = "ovs_friendly_fire";

    // The patterns live here, not in OpenVersus.toml: the mutator is the server's to turn on, not a player's.
    // Whole instructions from the game's only build, with rel32 targets and RIP-relative displacements as
    // wildcards; each matches once there (FriendlyFireHooksTests has the bytes). The functions are the C++
    // client's (StockDiagnostics.cpp, at RVAs 0x02951B00, 0x02957C10, 0x0121BFB0 and 0x02951A00).
    /// <summary>ProcessActiveHitInteraction's start.</summary>
    internal const string ProcessActiveHitInteractionPattern = "40 55 56 41 56 48 8D AC 24 30 F5 FF FF 48 81 EC D0 0B 00 00";
    /// <summary>UMvsDefenderComponent::GetHitResponseFlags's start.</summary>
    internal const string GetHitResponseFlagsPattern = "48 89 5C 24 10 48 89 6C 24 18 57 41 54 41 55 41 56 41 57 48 81 EC E0 0A 00 00";
    /// <summary>UMvsTeamComponent::IsSameTeam's start.</summary>
    internal const string IsSameTeamPattern = "48 85 D2 74 1F 80 7A 30 00 75 19 F7 42 08 00 00 00 60 75 10 8B 89 D0 00 00 00";
    /// <summary>UMvsAttackerComponent::GetTopLevelAttacker's start.</summary>
    internal const string GetTopLevelAttackerPattern = "48 89 5C 24 10 56 48 83 EC 20 48 8B F1 48 8B 89 A0 00 00 00";
    /// <summary>The shield code's team check (0x02953875), ending in its call to IsSameTeam, whose return address the C++ matched (0x0295388C).</summary>
    internal const string ShieldTeamCheckPattern = "80 79 30 00 75 19 F7 41 08 00 00 00 60 75 10 49 8B D6 E8 ? ? ? ?";
    /// <summary>Where the call to IsSameTeam is, from the start of <see cref="ShieldTeamCheckPattern"/>.</summary>
    internal const int ShieldTeamCheckCall = 18;

    // The instructions the entry hooks move, at least five bytes each.
    // push rbp; push rsi; push r14
    internal static readonly byte[] s_processActiveHitPrologue = [0x40, 0x55, 0x56, 0x41, 0x56];
    // mov [rsp+0x10], rbx
    internal static readonly byte[] s_getHitResponseFlagsPrologue = [0x48, 0x89, 0x5C, 0x24, 0x10];
    // push rbp; push rsi; push rdi; push r12
    private static readonly byte[] s_processEventPrologue = [0x40, 0x55, 0x56, 0x57, 0x41, 0x54];

    // Actors that keep the ally path, by exact class (the C++ client's list).
    // Raven's link pulse and Velma's books are the C++ client's; the game decides from Velma's marks.
    // Taz's dogpile with his "I Gotta Get In There!" perk is its own class (logged in the Lab,
    // 2026-09-28), and a teammate must be able to join it; the dogpile without the perk hits them.
    private static readonly string[] s_supportClassNames =
    [
        "Mvs_C025_Shine_Actor_C",
        "Mvs_Velma_Mark_Projectile_C", "Mvs_Velma_NoMark_Projectile_C", "Mvs_Velma_Book_Projectile_C",
        "MVS_TazPerkDogPile_SpawnedActor_C",
        "Mvs_Stripe_Buzzsaw_V2_C", // circles a teammate it passes through
        "MVS_C036_AgentAssist_Projectile_C", // Agent Smith's clone: touching a teammate makes it their assist
        "Mvs_LeBron_Basketball_C", // LeBron's pass: the same ball as his throw, so it never hurts a teammate
        "Mvs_WonderWoman_LassoProjectile_C", // Wonder Woman's lasso pulls a teammate in
        "Mvs_Rick_Polymorph_Bomb_C", // Rick's down special: it polymorphs a teammate, without the 1 damage (Lab log 2026-10-06)
        "Mvs_Finn_HighFiveShockwave_C", // the shockwave Finn's high five sends out after the slap (Lab log 2026-10-07)
    ];

    // Actors that, attached to a teammate (their equip component's AttachedComponents), make hits on
    // them ally interactions. Garnet's star over a teammate's head (Lab log 2026-09-29: her down
    // special through a starred teammate must not hurt; through one without the star it does).
    private static readonly string[] s_supportAttachmentNames =
    [
        "Mvs_Garnet_Star_Actor_C",
    ];

    // Moves a fighter makes with their own body, which do something to a teammate instead of hurting
    // them, by the animation montage their hitbox comes from (the hitbox's creating anim notify
    // state's outer). Tuggernuts's list, each checked in the Lab log (2026-09-29).
    private static readonly string[] s_supportMontageNames =
    [
        "Mvs_Jake_Special_N_Montage", // bite
        "Mvs_Jake_Horse_Intro_Montage", // horse
        "Mvs_Gizmo_Ground_Special_Up_Montage", // backpack (mount a teammate)
        "Mvs_Gizmo_Air_Special_Up_Montage", // backpack, in the air
        "Mvs_C027_Ground_Special_Air_AllyDash_Montage", // Nubia's teleport to a teammate
        "Mvs_C027_Air_Special_D_Montage", // Nubia's down special: to the teammate, then the circle
        "Mvs_C027_Ground_Special_D_Montage", // the circle after Nubia's teleport, on the ground
        "Mvs_Finn_Special_Ground_N", // Finn's neutral special, the high five (Lab log 2026-10-06; it has no air version)
    ];
    private const string JerryCork = "Mvs_JerryCork_Actor_C";
    private const string CarriedJerry = "Mvs_Jerry_Actor_NoHitbox_C";

    /// <summary>The most lines one match writes to <see cref="MatchRulesLog"/>; a repeat of the line before counts once.</summary>
    private const int MaxLoggedPerMatch = 2000;

    private static delegate* unmanaged<nint, nint, void> s_processActiveHit;
    private static delegate* unmanaged<nint, nint, byte, int*, void> s_getHitResponseFlags;
    private static delegate* unmanaged<nint, nint, byte> s_isSameTeam;
    private static delegate* unmanaged<nint, nint> s_getTopLevelAttacker;
    private static delegate* unmanaged<nint, nint, nint, void> s_processEvent;
    private static nint s_watchSlot;

    private static ILogger? s_log;
    private static ObjectFinder? s_finder;
    private static bool s_installed;
    private static bool s_offlineTesting;
    private static volatile bool s_active;
    private static nint s_fighterClass;
    private static FName[] s_supportClasses = [];
    private static FName[] s_supportMontages = [];
    private static FName[] s_supportAttachments = [];
    // The last hit kept for a support attachment, by attacker and defender component: the move's
    // follow-up hit (no hitbox of its own) comes right after, once the star is already used up.
    private static nint s_attachmentKeptAttacker;
    private static nint s_attachmentKeptDefender;
    private static FName s_cork;
    private static FName s_carriedJerry;
    private static nint s_gameState;
    private static bool s_gameStateFlagOk;
    private static bool s_matchStartedParamOk;
    private static int s_logged;
    // The last hit's answer (Classify), and the cost of the checks this match.
    private static nint s_lastHit, s_lastHitAttacker, s_lastHitDefender;
    private static Keep s_lastHitKeep;
    private static long s_checks, s_checkTicks, s_checkMaxTicks;
    private const int CostReportEvery = 2000;
    private static bool s_costReported;
    // Class pointer -> whether it is a fighter class, so the class chain is walked once per class.
    private static readonly Dictionary<nint, bool> s_fighterClasses = [];
    private static string? s_lastLine;
    private static int s_repeats;
    private static readonly Dictionary<nint, string> s_classNames = [];

    /// <summary>Which ally interactions keep the ally path, and why.</summary>
    private enum Keep
    {
        /// <summary>None: the hit becomes an enemy hit.</summary>
        No,
        /// <summary>The attacker is the defender's own, or held or carried by it.</summary>
        Own,
        /// <summary>The defender is not a fighter.</summary>
        NotFighter,
        /// <summary>One of the listed support actors.</summary>
        Support,
        /// <summary>A move from one of the listed montages.</summary>
        SupportMove,
        /// <summary>One of the two is riding, following or puppet-driven (Gizmo's backpack).</summary>
        Riding,
        /// <summary>The defender carries one of the listed attachments (Garnet's star), or this is that hit's follow-up.</summary>
        SupportAttachment,
    }

    /// <summary>
    /// Hooks the two hit functions and the shield's team check, and ProcessEvent through a filter
    /// for the match-start event. Nothing changes until <see cref="StartMatch"/> sees the mutator.
    /// False, with nothing hooked, when a pattern is missing; throws a <see cref="PatchException"/>
    /// when this build's code is not what the disassembly found. The ProcessEvent hook alone may
    /// fail (another mod on it), which only loses the match-start parameter.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        s_offlineTesting = c.Settings.FriendlyFireOffline;
        c.Log.Info("==Friendly Fire==");
        var image = c.Image;
        var processActiveHit = c.Patterns.Find("FriendlyFireHit", ProcessActiveHitInteractionPattern);
        var hitResponse = c.Patterns.Find("FriendlyFireResponse", GetHitResponseFlagsPattern);
        var isSameTeam = c.Patterns.Find("FriendlyFireIsSameTeam", IsSameTeamPattern);
        var topLevel = c.Patterns.Find("FriendlyFireTopLevelAttacker", GetTopLevelAttackerPattern);
        var shield = c.Patterns.Find("FriendlyFireShield", ShieldTeamCheckPattern);
        if (!processActiveHit.Found || !hitResponse.Found || !isSameTeam.Found || !topLevel.Found || !shield.Found)
        {
            c.Log.Error("Friendly fire: a function it needs was not found; nothing is hooked and the Friendly Fire mutator will not work");
            return false;
        }

        nint shieldCall = shield.Address + ShieldTeamCheckCall;
        if (CallSite.Destination(shieldCall) != isSameTeam.Address)
        {
            throw new PatchException($"the shield's team check at 0x{shieldCall:X} does not call IsSameTeam at 0x{isSameTeam.Address:X}");
        }

        s_isSameTeam = (delegate* unmanaged<nint, nint, byte>)isSameTeam.Address;
        s_getTopLevelAttacker = (delegate* unmanaged<nint, nint>)topLevel.Address;
        GameFunctions.Register("UMvsTeamComponent::IsSameTeam", isSameTeam, FunctionSource.Pattern, "FriendlyFireIsSameTeam", "bool UMvsTeamComponent::IsSameTeam(UMvsTeamComponent* this, UMvsTeamComponent* other)", image);
        GameFunctions.Register("UMvsAttackerComponent::GetTopLevelAttacker", topLevel, FunctionSource.Pattern, "FriendlyFireTopLevelAttacker", "UMvsAttackerComponent* GetTopLevelAttacker(UMvsAttackerComponent* this)", image);

        s_processActiveHit = (delegate* unmanaged<nint, nint, void>)Hook(image, "ProcessActiveHitInteraction", processActiveHit, s_processActiveHitPrologue,
            (nint)(delegate* unmanaged<nint, nint, void>)&ProcessActiveHitInteraction, "void ProcessActiveHitInteraction(UMvsAttackerComponent* attacker, FActiveHitInteraction* hit)");
        s_getHitResponseFlags = (delegate* unmanaged<nint, nint, byte, int*, void>)Hook(image, "UMvsDefenderComponent::GetHitResponseFlags", hitResponse, s_getHitResponseFlagsPrologue,
            (nint)(delegate* unmanaged<nint, nint, byte, int*, void>)&GetHitResponseFlags, "void UMvsDefenderComponent::GetHitResponseFlags(UMvsDefenderComponent* this, const FActiveHitInteraction* hit, bool isAlly, int32* flags)");
        RedirectShieldCheckWithHit(shieldCall);

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
        c.Log.Success($"Friendly fire hooked (shield check at 0x{shieldCall:X}); it is on only in matches with the Friendly Fire mutator");
        return true;
    }

    /// <summary>
    /// Points the shield's IsSameTeam call at <see cref="ShieldIsSameTeam"/> through a stub that also passes the hit
    /// being checked. The shield code (in the function at 0x02953020, which takes the FActiveHitInteraction as its
    /// second argument and keeps it in r15: mov r15, rdx; it reads [r15+0x330], the defender, just before) runs before
    /// ProcessActiveHitInteraction, so the hit is not otherwise known yet. The stub's prelude is mov r8, r15.
    /// </summary>
    private static void RedirectShieldCheckWithHit(nint shieldCall) =>
        CallSite.InjectWithPrelude(shieldCall, s_passHitInR8, (nint)(delegate* unmanaged<nint, nint, nint, byte>)&ShieldIsSameTeam, jump: false);

    // mov r8, r15
    private static readonly byte[] s_passHitInR8 = [0x4D, 0x89, 0xF8];

    private static nint Hook(GameImage image, string name, PatternHit hit, byte[] prologue, nint hook, string declaration)
    {
        nint gateway = EntryHook.Install(hit.Address, prologue, hook);
        GameFunctions.Register(name, gateway, FunctionSource.Pattern, $"{hit.Name} at 0x{hit.Address:X}, entry hooked; this is the gateway to the original", declaration, image);
        return gateway;
    }

    /// <summary>
    /// A new match (<see cref="StockRulesHooks"/>, on the game thread): friendly fire is on when the
    /// server selected the mutator, off otherwise, including when the settings could not be read.
    /// </summary>
    public static void StartMatch(ObjectFinder finder, nint gameMode, MatchSettings? settings)
    {
        ReportCost("last match");
        s_costReported = false;
        s_active = false;
        s_logged = 0;
        s_checks = s_checkTicks = s_checkMaxTicks = 0;
        s_lastHit = 0;
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
        s_supportClasses = s_supportClassNames.Select(finder.Names.Find).ToArray();
        s_supportMontages = s_supportMontageNames.Select(finder.Names.Find).ToArray();
        s_supportAttachments = s_supportAttachmentNames.Select(finder.Names.Find).ToArray();
        s_attachmentKeptAttacker = s_attachmentKeptDefender = 0;
        s_cork = finder.Names.Find(JerryCork);
        s_carriedJerry = finder.Names.Find(CarriedJerry);
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
        MatchRulesLog.Line($"friendly fire: ON (support classes loaded: {string.Join(", ", s_supportClassNames.Where((_, i) => s_supportClasses[i].Index != 0))}; montages: {string.Join(", ", s_supportMontageNames.Where((_, i) => s_supportMontages[i].Index != 0))}; cork={s_cork.Index != 0} carriedJerry={s_carriedJerry.Index != 0})");
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

    [UnmanagedCallersOnly]
    private static void ProcessActiveHitInteraction(nint attackerComponent, nint hit)
    {
        if (!s_active)
        {
            s_processActiveHit(attackerComponent, hit);
            return;
        }

        HookGuard.Run("FriendlyFireHit", hit, static h => OnHit(h));
        s_processActiveHit(attackerComponent, hit);
    }

    [UnmanagedCallersOnly]
    private static void GetHitResponseFlags(nint defender, nint hit, byte isAlly, int* flags)
    {
        byte asked = isAlly;
        if (s_active && isAlly != 0)
        {
            asked = HookGuard.Run("FriendlyFireResponse", hit, static h => Classify(h) == Keep.No ? (byte)0 : (byte)1, isAlly);
        }

        s_getHitResponseFlags(defender, hit, asked, flags);
        if (s_active && isAlly != 0 && MatchRulesLog.On)
        {
            int after = flags != null && CodeWriter.TryRead((nint)flags, out int read) ? read : 0;
            HookGuard.Run("FriendlyFireResponse", (hit, asked, flags: after), static s => LogHit("response", s.hit, s.asked == 0 ? Keep.No : Classify(s.hit), $" flags=0x{s.flags:X}"));
        }
    }

    [UnmanagedCallersOnly]
    private static byte ShieldIsSameTeam(nint team, nint other, nint hit)
    {
        byte same = s_isSameTeam(team, other);
        if (same == 0 || !s_active || team == other)
        {
            return same;
        }

        // A kept ally move (Jake's bite, say) reaching a shielding teammate stays an ally's: the shield would
        // otherwise take it as an opponent's and the teammate was hurt (Tuggernuts, 2026-10-07). The hit is the shield
        // code's own (see RedirectShieldCheckWithHit), the same one ProcessActiveHitInteraction classifies next.
        var keep = hit == 0 ? Keep.No : HookGuard.Run("FriendlyFireShield", hit, static h => Classify(h), Keep.No);
        if (MatchRulesLog.On)
        {
            LogLine(keep != Keep.No
                ? $"ff shield check: the hit is kept ({keep}), teammates stay teammates"
                : "ff shield check: teammates count as opponents");
        }

        return keep != Keep.No ? same : (byte)0;
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

    /// <summary>An ally interaction turns into an enemy hit unless it is one to keep.</summary>
    private static void OnHit(nint hit)
    {
        EnsureGameStateFlag();
        if (!CodeWriter.TryRead(hit + Mvs.HitInteractionIsAlly, out byte ally) || ally != 1)
        {
            return;
        }

        var keep = Classify(hit);
        if (keep == Keep.No && !CodeWriter.TryWrite(hit + Mvs.HitInteractionIsAlly, (byte)0))
        {
            LogLine($"ff hit 0x{hit:X}: IsAllyInteraction could not be cleared");
        }

        if (MatchRulesLog.On)
        {
            LogHit("hit", hit, keep, "");
        }
    }

    /// <summary>
    /// Whether an ally interaction keeps the ally path, and why; <see cref="Keep.No"/> when it does
    /// not. One hit is asked about up to three times (the hit, its response flags, the log), so the
    /// answer for the last hit is kept and given again. Every check reads game memory through a
    /// guarded call, so the cheap and common ones come first: a fighter's own actor touching them
    /// (most ally interactions), a defender that is not a fighter, then the listed classes, moves
    /// and attachments, and the ride and attachment walks last. Timed for <see cref="MatchRulesLog"/>.
    /// </summary>
    private static Keep Classify(nint hit)
    {
        nint attacker = Read(hit, Mvs.HitInteractionAttacker);
        nint defender = Read(hit, Mvs.HitInteractionDefender);
        if (hit == s_lastHit && attacker == s_lastHitAttacker && defender == s_lastHitDefender)
        {
            return s_lastHitKeep;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var keep = Decide(Parties.Read(hit));
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        s_checks++;
        s_checkTicks += ticks;
        s_checkMaxTicks = Math.Max(s_checkMaxTicks, ticks);
        if (s_checks % CostReportEvery == 0)
        {
            LogCost("so far");
        }

        (s_lastHit, s_lastHitAttacker, s_lastHitDefender, s_lastHitKeep) = (hit, attacker, defender, keep);
        return keep;
    }

    private static Keep Decide(Parties p)
    {
        nint defender = p.DefenderOwner;
        if (defender == 0)
        {
            return Keep.No;
        }

        // The Garnet follow-up memory ends when its attacker lands a hit that has a hitbox, whichever check answers first.
        if (p.Attacker == s_attachmentKeptAttacker && p.ColliderSet != 0 && !HasSupportAttachment(defender))
        {
            s_attachmentKeptAttacker = s_attachmentKeptDefender = 0;
        }

        if (p.TopOwner == defender)
        {
            return Keep.Own;
        }

        if (!IsFighterObject(defender))
        {
            return Keep.NotFighter;
        }

        if (IsSupport(p.AttackerOwner))
        {
            return Keep.Support;
        }

        if (IsSupportMove(p.ColliderSet))
        {
            return Keep.SupportMove;
        }

        if (Read(p.AttackerOwner, Mvs.ActorOwner) == defender || Read(p.TopOwner, Mvs.ActorOwner) == defender
            || IsEquipAttachedTo(p.AttackerOwner, defender))
        {
            return Keep.Own;
        }

        if (IsSupportAttachmentHit(p))
        {
            return Keep.SupportAttachment;
        }

        if (IsPuppeted(p.TopOwner) || IsPuppeted(defender)
            || Rides(p.TopOwner, defender) || Rides(defender, p.TopOwner)
            || Follows(p.TopOwner, defender) || Follows(defender, p.TopOwner))
        {
            return Keep.Riding;
        }

        return IsOwn(p) ? Keep.Own : Keep.No;
    }

    /// <summary>
    /// Writes this match's check cost once it ends (the game's match end, or the game closing), so a
    /// session that ends after one match still has it. Once per match; the next match starts over.
    /// </summary>
    public static void ReportCost(string when)
    {
        if (!s_costReported)
        {
            LogCost(when);
            s_costReported = s_checks > 0;
        }
    }

    /// <summary>How long the checks took this match, for <see cref="MatchRulesLog"/>.</summary>
    private static void LogCost(string when)
    {
        if (s_checks == 0)
        {
            return;
        }

        double us = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        MatchRulesLog.Line($"ff cost ({when}): {s_checks} checks, {s_checkTicks * us / s_checks:F1} us each, worst {s_checkMaxTicks * us:F1} us, {s_checkTicks * us / 1000:F1} ms in all");
    }

    /// <summary>
    /// Whether fighter <paramref name="rider"/> is riding <paramref name="mount"/> (Gizmo on a
    /// teammate's back): its rider component's CurrentRide is the mount or one of its components.
    /// </summary>
    private static bool Rides(nint rider, nint mount)
    {
        nint ride = CurrentRide(rider);
        return ride != 0 && mount != 0 && (ride == mount || Read(ride, Mvs.ActorComponentOwner) == mount);
    }

    /// <summary>What fighter <paramref name="fighter"/> is riding (UMvsRiderComponent.CurrentRide), or 0; 0 for anything that is not a fighter.</summary>
    private static nint CurrentRide(nint fighter)
    {
        if (!IsFighterObject(fighter))
        {
            return 0;
        }

        return Read(Read(fighter, FixedCharacterRiderComponent), RiderCurrentRide);
    }

    /// <summary>
    /// Whether fighter <paramref name="follower"/> is following <paramref name="leader"/>: its
    /// UMvsFollowerComponentBase (through the pawn's component cache) is following and its
    /// LeaderActor is the leader. The rider component stays empty on Gizmo's backpack (Lab, 2026-09-29).
    /// </summary>
    private static bool Follows(nint follower, nint leader) =>
        leader != 0 && FollowerComponent(follower) is var component and not 0
        && CodeWriter.TryRead(component + FollowerIsFollowing, out byte following) && following != 0
        && Read(component, FollowerLeaderActor) == leader;

    /// <summary>
    /// Whether fighter <paramref name="fighter"/> is being driven through its puppet component, as
    /// Gizmo is on a teammate's back (driver MvsSpawnedActor_GizmoBackpack_C, puppet state 2; Lab log
    /// 2026-09-29). Only ally interactions get here, so this keeps the two teammates' hits on each
    /// other on the ally path while the link lasts.
    /// </summary>
    private static bool IsPuppeted(nint fighter) =>
        IsFighterObject(fighter) && Read(Read(fighter, PawnPuppet), PuppetDriver) != 0;

    /// <summary>A fighter's UMvsFollowerComponentBase, or 0.</summary>
    private static nint FollowerComponent(nint fighter) =>
        IsFighterObject(fighter) ? Read(Read(fighter, PawnComponentCache), ComponentCacheFollower) : 0;

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

    // APfgFixedPawn.ComponentCache, UMvsComponentCache.FollowerComponent, and
    // UMvsFollowerComponentBase.LeaderActor / bIsFollowing (CXXHeaderDump).
    private const int PawnComponentCache = 0x388;
    private const int ComponentCacheFollower = 0x128;
    private const int FollowerLeaderActor = 0xF8;
    private const int FollowerIsFollowing = 0x13A;

    // AMvsFixedCharacter.RiderComponent and UMvsRiderComponent.CurrentRide / CurrentRideStatus (CXXHeaderDump).
    private const int FixedCharacterRiderComponent = 0x480;
    private const int RiderCurrentRide = 0x108;
    private const int RiderCurrentRideStatus = 0x110;

    /// <summary>
    /// A hit on a teammate carrying one of <see cref="s_supportAttachmentNames"/>, or the follow-up of
    /// such a hit: a hit from the same attacker component on the same defender with no hitbox of its
    /// own, until that attacker lands a hit that has one (its next move). The same answer however
    /// often one hit is asked about, and decided by the order of the hits, which every peer
    /// simulates alike.
    /// </summary>
    private static bool IsSupportAttachmentHit(Parties p)
    {
        if (HasSupportAttachment(p.DefenderOwner))
        {
            s_attachmentKeptAttacker = p.Attacker;
            s_attachmentKeptDefender = p.DefenderOwner;
            return true;
        }

        if (p.Attacker != s_attachmentKeptAttacker)
        {
            return false;
        }

        if (p.ColliderSet != 0)
        {
            s_attachmentKeptAttacker = s_attachmentKeptDefender = 0;
            return false;
        }

        return p.DefenderOwner == s_attachmentKeptDefender;
    }

    /// <summary>
    /// Whether <paramref name="actor"/> is attached to fighter <paramref name="fighter"/> through the
    /// fighter's equip component: Steven's tether, while connected to a teammate, explodes on them
    /// (Lab log 2026-09-29: must not hurt connected, does unconnected).
    /// </summary>
    private static bool IsEquipAttachedTo(nint actor, nint fighter) =>
        actor != 0 && IsFighterObject(fighter)
        && ReadArray(Read(Read(fighter, PawnComponentCache), ComponentCacheEquip), EquipAttachedComponents, 16)
            .Any(component => Read(component, Mvs.ActorComponentOwner) == actor);

    /// <summary>Whether fighter <paramref name="fighter"/> has an actor of one of <see cref="s_supportAttachmentNames"/> attached through its equip component.</summary>
    private static bool HasSupportAttachment(nint fighter) =>
        IsFighterObject(fighter)
        && ReadArray(Read(Read(fighter, PawnComponentCache), ComponentCacheEquip), EquipAttachedComponents, 16)
            .Any(component => s_supportAttachments.Any(a => ClassIs(Read(component, Mvs.ActorComponentOwner), a)));

    /// <summary>Whether the hitbox comes from one of <see cref="s_supportMontageNames"/>.</summary>
    private static bool IsSupportMove(nint colliderSet)
    {
        nint montage = Read(Read(colliderSet, ColliderSetUpdateObject), Mvs.ObjectOuterPrivate);
        return montage != 0 && CodeWriter.TryRead(montage + Mvs.ObjectNamePrivate, out FName name) && s_supportMontages.Any(m => Is(name, m));
    }

    /// <summary>
    /// The defender's own attack, or one it holds or carries: the attacker's top-level owner is the
    /// defender, or either attacker's current Owner is (Unreal moves Owner to whoever holds a caught
    /// or thrown actor, while the Instigator stays), or an attacker is attached to the defender.
    /// </summary>
    private static bool IsOwn(Parties p)
    {
        nint defender = p.DefenderOwner;
        nint directHolder = Read(p.AttackerOwner, Mvs.ActorOwner);
        nint topHolder = Read(p.TopOwner, Mvs.ActorOwner);
        return p.TopOwner == defender || directHolder == defender || topHolder == defender
            || IsAttachedTo(p.AttackerOwner, defender) || IsAttachedTo(directHolder, defender)
            || IsAttachedTo(p.TopOwner, defender) || IsAttachedTo(topHolder, defender);
    }

    /// <summary>One of <see cref="s_supportClassNames"/> (Raven's pulse, Velma's books, Taz's perk dogpile), or Jerry's cork while Jerry is carried.</summary>
    private static bool IsSupport(nint attacker)
    {
        if (attacker == 0 || !CodeWriter.TryRead(attacker + Mvs.ObjectClassPrivate, out nint cls) || !CodeWriter.TryRead(cls + Mvs.ObjectNamePrivate, out FName name))
        {
            return false;
        }

        if (s_supportClasses.Any(c => Is(name, c)))
        {
            return true;
        }

        return Is(name, s_cork) && ClassIs(Read(attacker, Mvs.ActorOwner), s_carriedJerry);
    }

    private static bool ClassIs(nint obj, FName expected) =>
        obj != 0 && CodeWriter.TryRead(obj + Mvs.ObjectClassPrivate, out nint cls) && CodeWriter.TryRead(cls + Mvs.ObjectNamePrivate, out FName name) && Is(name, expected);

    private static bool Is(FName name, FName expected) => expected.Index != 0 && name.Index == expected.Index && name.Number == expected.Number;

    /// <summary>Whether <paramref name="actor"/> is attached to <paramref name="parent"/>, up to three levels (RootComponent, AttachParent, its owner).</summary>
    private static bool IsAttachedTo(nint actor, nint parent)
    {
        if (actor == 0 || parent == 0)
        {
            return false;
        }

        nint current = actor, previous = 0;
        for (int depth = 0; depth < 3; depth++)
        {
            nint next = Read(Read(Read(current, Mvs.ActorRootComponent), Mvs.SceneComponentAttachParent), Mvs.ActorComponentOwner);
            if (next == 0 || next == current || next == previous)
            {
                return false;
            }

            if (next == parent)
            {
                return true;
            }

            previous = current;
            current = next;
        }

        return false;
    }

    /// <summary>The actor <paramref name="actor"/>'s root component is attached to, or 0.</summary>
    private static nint AttachParentActor(nint actor) => Read(Read(Read(actor, Mvs.ActorRootComponent), Mvs.SceneComponentAttachParent), Mvs.ActorComponentOwner);

    private static nint Read(nint obj, int offset) => obj != 0 && CodeWriter.TryRead(obj + offset, out nint value) ? value : 0;

    /// <summary>The actors behind one hit: the attacker component's owner, its top-level attacker's owner, and the defender's.</summary>
    private readonly record struct Parties(nint Attacker, nint AttackerOwner, nint TopOwner, nint DefenderOwner, nint ColliderSet)
    {
        public static Parties Read(nint hit)
        {
            nint attacker = FriendlyFireHooks.Read(hit, Mvs.HitInteractionAttacker);
            nint defender = FriendlyFireHooks.Read(hit, Mvs.HitInteractionDefender);
            nint top = attacker != 0 ? s_getTopLevelAttacker(attacker) : 0;
            if (top == 0)
            {
                top = attacker;
            }

            return new Parties(attacker, FriendlyFireHooks.Read(attacker, Mvs.ActorComponentOwner), FriendlyFireHooks.Read(top, Mvs.ActorComponentOwner),
                FriendlyFireHooks.Read(defender, Mvs.ActorComponentOwner), FriendlyFireHooks.Read(hit, Mvs.HitInteractionColliderSet));
        }
    }

    /// <summary>
    /// One hit for <see cref="MatchRulesLog"/>, with the class of every actor involved and the hitbox
    /// set that connected: what a new exception (such as Taz's dogpile with its perk) is built from.
    /// </summary>
    private static void LogHit(string where, nint hit, Keep keep, string extra)
    {
        if (s_logged >= MaxLoggedPerMatch)
        {
            return;
        }

        var p = Parties.Read(hit);
        nint holder = Read(p.AttackerOwner, Mvs.ActorOwner);
        nint instigator = Read(p.AttackerOwner, Mvs.ActorInstigator);
        LogLine($"ff {where}: {(keep == Keep.No ? "enemy" : "ally, " + keep)} attacker={ClassName(p.AttackerOwner)} top={ClassName(p.TopOwner)} "
            + $"holder={ClassName(holder)} instigator={ClassName(instigator)} defender={ClassName(p.DefenderOwner)} {DescribeHitbox(p.ColliderSet)} "
            + $"rides={DescribeRide(p.TopOwner)}/{DescribeRide(p.DefenderOwner)} follows={DescribeFollow(p.TopOwner)}/{DescribeFollow(p.DefenderOwner)} "
            + $"attached={ClassName(AttachParentActor(p.TopOwner))}/{ClassName(AttachParentActor(p.DefenderOwner))}{extra}");
        if (keep == Keep.No && where == "hit" && IsFighterObject(p.TopOwner))
        {
            // Until the backpack's link is known: the state each fighter carries, to find it.
            LogLine($"ff state: attacker {DescribeState(p.TopOwner)}");
            LogLine($"ff state: defender {DescribeState(p.DefenderOwner)}");
        }
    }

    /// <summary>A fighter's puppet link, active buffs and state tags, for the log.</summary>
    private static string DescribeState(nint fighter)
    {
        if (!IsFighterObject(fighter))
        {
            return "-";
        }

        nint puppet = Read(fighter, PawnPuppet);
        CodeWriter.TryRead(puppet + PuppetState, out byte puppetState);
        CodeWriter.TryRead(puppet + PuppetCurrentVictims + 8, out int victims);
        nint cache = Read(fighter, PawnComponentCache);
        var buffs = ReadArray(Read(cache, ComponentCacheBuff), BuffActiveBuffs, 16).Select(ClassName);
        var tags = ReadNames(Read(cache, ComponentCacheTags) + StateTagsTags, 40);
        nint equip = Read(cache, ComponentCacheEquip);
        nint item = Read(equip, EquipPrimaryItemSlot);
        var attachedToIt = ReadArray(equip, EquipAttachedComponents, 16).Select(c => $"{ClassName(c)}({ClassName(Read(c, Mvs.ActorComponentOwner))})");
        return $"{ClassName(fighter)} puppet(state{puppetState} driver={ClassName(Read(Read(puppet, PuppetDriver), Mvs.ActorComponentOwner))} victims={victims}) "
            + $"item={ClassName(item)}({ClassName(Read(item, Mvs.ActorComponentOwner))}) equipAttached=[{string.Join(",", attachedToIt)}] "
            + $"buffs=[{string.Join(",", buffs)}] tags=[{string.Join(",", tags)}]";
    }

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

    /// <summary>The FNames of a TArray of FGameplayTag (an FName each) at <paramref name="array"/>, at most <paramref name="max"/>.</summary>
    private static List<string> ReadNames(nint array, int max)
    {
        var names = new List<string>();
        if (CodeWriter.TryRead(array, out nint data) && data != 0 && CodeWriter.TryRead(array + 8, out int count))
        {
            for (int i = 0; i < Math.Min(count, max); i++)
            {
                if (CodeWriter.TryRead(data + i * 8, out FName name))
                {
                    names.Add(s_finder?.Names.ToString(name) ?? $"#{name.Index}");
                }
            }
        }

        return names;
    }

    // APfgFixedPawn.Puppet; UPfgPuppetComponent.CurrentVictims / PuppetState / PuppetDriver;
    // UMvsComponentCache.BuffComponent, UPfgComponentCache.TagComponent; UMvsBuffComponent.ActiveBuffs;
    // UPfgStateTagComponent.Tags (FGameplayTagContainer: GameplayTags first) (CXXHeaderDump).
    private const int PawnPuppet = 0x370;
    private const int PuppetCurrentVictims = 0x140;
    private const int PuppetState = 0x158;
    private const int PuppetDriver = 0x1B0;
    private const int ComponentCacheBuff = 0x138;
    private const int ComponentCacheEquip = 0x130;
    // UMvsEquipComponent.AttachedComponents / PrimaryItemSlot (CXXHeaderDump).
    private const int EquipAttachedComponents = 0xF0;
    private const int EquipPrimaryItemSlot = 0x100;
    private const int ComponentCacheTags = 0xB0;
    private const int BuffActiveBuffs = 0xF8;
    private const int StateTagsTags = 0x130;

    /// <summary>
    /// What names the move behind a hitbox (a UPfgColliderSetComponent, whose own name is a throwaway):
    /// its collider-set asset (InitData), the object that created it (UpdateObject) and that object's
    /// outer, and the first shape's name. Offsets from PfgFixed2DGame in the CXXHeaderDump.
    /// </summary>
    private static string DescribeHitbox(nint colliderSet)
    {
        if (colliderSet == 0)
        {
            return "hitbox=-";
        }

        nint initData = Read(colliderSet, ColliderSetInitData);
        nint updateObject = Read(colliderSet, ColliderSetUpdateObject);
        nint updateOuter = Read(updateObject, Mvs.ObjectOuterPrivate);
        return $"hitbox={ObjectName(initData)} by={ClassName(updateObject)}:{ObjectName(updateObject)} in={ClassName(updateOuter)}:{ObjectName(updateOuter)} "
            + $"shape={FirstShapeName(colliderSet, ColliderSetShapeEntries)}/{FirstShapeName(colliderSet, ColliderSetInitDataDirect)}";
    }

    /// <summary>Whom a fighter's follower component follows, and whether it is following, for the log; "-" without one.</summary>
    private static string DescribeFollow(nint fighter)
    {
        nint component = FollowerComponent(fighter);
        if (component == 0)
        {
            return "-";
        }

        CodeWriter.TryRead(component + FollowerIsFollowing, out byte following);
        return $"{ClassName(Read(component, FollowerLeaderActor))},following{following}";
    }

    /// <summary>What a fighter rides, and the ride status, for the log; "-" when nothing.</summary>
    private static string DescribeRide(nint fighter)
    {
        nint ride = CurrentRide(fighter);
        if (ride == 0)
        {
            return "-";
        }

        CodeWriter.TryRead(Read(fighter, FixedCharacterRiderComponent) + RiderCurrentRideStatus, out byte status);
        return $"{ClassName(ride)}({ClassName(Read(ride, Mvs.ActorComponentOwner))}),status{status}";
    }

    private static string FirstShapeName(nint colliderSet, int entriesOffset)
    {
        // TArray<FPfgColliderSetEntry>: data, count; each entry starts with its shape's FName.
        nint data = Read(colliderSet, entriesOffset);
        return data != 0 && CodeWriter.TryRead(colliderSet + entriesOffset + 8, out int count) && count > 0 && CodeWriter.TryRead(data, out FName name)
            ? s_finder?.Names.ToString(name) ?? $"#{name.Index}"
            : "-";
    }

    // UPfgColliderSetComponent (PfgFixed2DGame, CXXHeaderDump).
    private const int ColliderSetUpdateObject = 0x520;
    private const int ColliderSetInitData = 0x558;
    private const int ColliderSetShapeEntries = 0x560;
    private const int ColliderSetInitDataDirect = 0x580;

    /// <summary>
    /// Writes <paramref name="line"/> unless it repeats the one before (a tether or lasso connects every
    /// frame); the repeats are counted onto the next different line. At most <see cref="MaxLoggedPerMatch"/> lines.
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
