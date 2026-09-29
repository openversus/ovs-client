using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
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

    // RVAs are the C++ client's; the bytes are from a disassembly of the final build
    // (MultiVersus-Win64-Shipping.exe, 2026-09-28).
    private const uint ProcessActiveHitInteractionRva = 0x02951B00;
    private const uint GetHitResponseFlagsRva = 0x02957C10;
    private const uint IsSameTeamRva = 0x0121BFB0;
    private const uint GetTopLevelAttackerRva = 0x02951A00;
    /// <summary>The call to IsSameTeam in the shield code, whose return address the C++ matched (0x0295388C).</summary>
    private const uint ShieldTeamCheckCallRva = 0x02953887;

    // push rbp; push rsi; push r14
    private static readonly byte[] s_processActiveHitPrologue = [0x40, 0x55, 0x56, 0x41, 0x56];
    // mov [rsp+0x10], rbx
    private static readonly byte[] s_getHitResponseFlagsPrologue = [0x48, 0x89, 0x5C, 0x24, 0x10];
    // test rdx, rdx; je; cmp byte [rdx+0x30], 0
    private static readonly byte[] s_isSameTeamCode = [0x48, 0x85, 0xD2, 0x74, 0x1F, 0x80, 0x7A, 0x30, 0x00];
    // mov [rsp+0x10], rbx; push rsi; sub rsp, 0x20; mov rsi, rcx; mov rcx, [rcx+0xA0]
    private static readonly byte[] s_getTopLevelAttackerCode = [0x48, 0x89, 0x5C, 0x24, 0x10, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xF1, 0x48, 0x8B, 0x89, 0xA0, 0x00, 0x00, 0x00];
    // mov rdx, r14; call IsSameTeam
    private static readonly byte[] s_shieldCall = [0x49, 0x8B, 0xD6, 0xE8];
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
    ];

    // Moves a fighter makes with their own body, which do something to a teammate instead of hurting
    // them, by the animation montage their hitbox comes from (the hitbox's creating anim notify
    // state's outer). Jacob's list, each checked in the Lab log (2026-09-29).
    private static readonly string[] s_supportMontageNames =
    [
        "Mvs_Jake_Special_N_Montage", // bite
        "Mvs_Jake_Horse_Intro_Montage", // horse
        "Mvs_Gizmo_Ground_Special_Up_Montage", // backpack (mount a teammate)
        "Mvs_Gizmo_Air_Special_Up_Montage", // backpack, in the air
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
    private static FName s_cork;
    private static FName s_carriedJerry;
    private static nint s_gameState;
    private static bool s_gameStateFlagOk;
    private static bool s_matchStartedParamOk;
    private static int s_logged;
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
        /// <summary>One of the two is riding or following the other (Gizmo's backpack).</summary>
        Riding,
    }

    /// <summary>
    /// Hooks the two hit functions and the shield's team check, and ProcessEvent through a filter
    /// for the match-start event. Nothing changes until <see cref="StartMatch"/> sees the mutator.
    /// Throws a <see cref="PatchException"/> when this build's code is not what the disassembly
    /// found; the ProcessEvent hook alone may fail (another mod on it), which only loses the
    /// match-start parameter.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        s_offlineTesting = c.Settings.FriendlyFireOffline;
        c.Log.Info("==Friendly Fire==");
        var image = c.Image;
        nint isSameTeam = image.Address(IsSameTeamRva);
        nint topLevel = image.Address(GetTopLevelAttackerRva);
        nint shieldCall = image.Address(ShieldTeamCheckCallRva);
        CodeWriter.Expect(isSameTeam, s_isSameTeamCode);
        CodeWriter.Expect(topLevel, s_getTopLevelAttackerCode);
        CodeWriter.Expect(shieldCall - 3, s_shieldCall);
        if (CallSite.Destination(shieldCall) != isSameTeam)
        {
            throw new PatchException($"the shield's team check at 0x{shieldCall:X} does not call IsSameTeam at 0x{isSameTeam:X}");
        }

        CodeWriter.Expect(image.Address(ProcessActiveHitInteractionRva), s_processActiveHitPrologue);
        CodeWriter.Expect(image.Address(GetHitResponseFlagsRva), s_getHitResponseFlagsPrologue);

        s_isSameTeam = (delegate* unmanaged<nint, nint, byte>)isSameTeam;
        s_getTopLevelAttacker = (delegate* unmanaged<nint, nint>)topLevel;
        GameFunctions.FromRva("UMvsTeamComponent::IsSameTeam", IsSameTeamRva, "bool UMvsTeamComponent::IsSameTeam(UMvsTeamComponent* this, UMvsTeamComponent* other)", image);
        GameFunctions.FromRva("UMvsAttackerComponent::GetTopLevelAttacker", GetTopLevelAttackerRva, "UMvsAttackerComponent* GetTopLevelAttacker(UMvsAttackerComponent* this)", image);

        s_processActiveHit = (delegate* unmanaged<nint, nint, void>)Hook(image, "ProcessActiveHitInteraction", ProcessActiveHitInteractionRva, s_processActiveHitPrologue,
            (nint)(delegate* unmanaged<nint, nint, void>)&ProcessActiveHitInteraction, "void ProcessActiveHitInteraction(UMvsAttackerComponent* attacker, FActiveHitInteraction* hit)");
        s_getHitResponseFlags = (delegate* unmanaged<nint, nint, byte, int*, void>)Hook(image, "UMvsDefenderComponent::GetHitResponseFlags", GetHitResponseFlagsRva, s_getHitResponseFlagsPrologue,
            (nint)(delegate* unmanaged<nint, nint, byte, int*, void>)&GetHitResponseFlags, "void UMvsDefenderComponent::GetHitResponseFlags(UMvsDefenderComponent* this, const FActiveHitInteraction* hit, bool isAlly, int32* flags)");
        CallSite.Redirect(shieldCall, (nint)(delegate* unmanaged<nint, nint, byte>)&ShieldIsSameTeam);

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
        c.Log.Success("Friendly fire hooked; it is on only in matches with the Friendly Fire mutator");
        return true;
    }

    private static nint Hook(GameImage image, string name, uint rva, byte[] prologue, nint hook, string declaration)
    {
        nint gateway = EntryHook.Install(image.Address(rva), prologue, hook);
        GameFunctions.Register(name, gateway, FunctionSource.Rva, $"rva 0x{rva:X}, entry hooked; this is the gateway to the original", declaration, image);
        return gateway;
    }

    /// <summary>
    /// A new match (<see cref="StockRulesHooks"/>, on the game thread): friendly fire is on when the
    /// server selected the mutator, off otherwise, including when the settings could not be read.
    /// </summary>
    public static void StartMatch(ObjectFinder finder, nint gameMode, MatchSettings? settings)
    {
        s_active = false;
        s_logged = 0;
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
        if (s_active)
        {
            HookGuard.Run("FriendlyFireHit", hit, static h => OnHit(h));
        }

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
            HookGuard.Run("FriendlyFireResponse", (hit, asked, flags: flags != null ? *flags : 0), static s => LogHit("response", s.hit, s.asked == 0 ? Keep.No : Classify(s.hit), $" flags=0x{s.flags:X}"));
        }
    }

    [UnmanagedCallersOnly]
    private static byte ShieldIsSameTeam(nint team, nint other)
    {
        byte same = s_isSameTeam(team, other);
        if (same == 0 || !s_active || team == other)
        {
            return same;
        }

        if (MatchRulesLog.On)
        {
            LogLine("ff shield check: teammates count as opponents");
        }

        return 0;
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

        byte* friendlyFire = (byte*)(parameters + Mvs.MatchStartedFriendlyFireParam);
        byte was = *friendlyFire;
        *friendlyFire = 1;
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

        byte* flag = (byte*)(s_gameState + Mvs.PandaGameStateFriendlyFire);
        if (CodeWriter.TryRead(s_gameState + Mvs.PandaGameStateFriendlyFire, out byte was) && was != 1)
        {
            *flag = 1;
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
        if (keep == Keep.No)
        {
            *(byte*)(hit + Mvs.HitInteractionIsAlly) = 0;
        }

        if (MatchRulesLog.On)
        {
            LogHit("hit", hit, keep, "");
        }
    }

    /// <summary>Whether an ally interaction keeps the ally path, and why; <see cref="Keep.No"/> when it does not.</summary>
    private static Keep Classify(nint hit)
    {
        var parties = Parties.Read(hit);
        if (parties.DefenderOwner == 0)
        {
            return Keep.No;
        }

        if (IsOwn(parties))
        {
            return Keep.Own;
        }

        if (Rides(parties.TopOwner, parties.DefenderOwner) || Rides(parties.DefenderOwner, parties.TopOwner)
            || Follows(parties.TopOwner, parties.DefenderOwner) || Follows(parties.DefenderOwner, parties.TopOwner))
        {
            return Keep.Riding;
        }

        if (s_finder is { } finder && ObjectHeader.TryRead(finder.Memory, parties.DefenderOwner, out var defender)
            && !ObjectFinder.Inherits(finder.Memory, defender.ClassPrivate, s_fighterClass))
        {
            return Keep.NotFighter;
        }

        if (IsSupport(parties.AttackerOwner))
        {
            return Keep.Support;
        }

        return IsSupportMove(parties.ColliderSet) ? Keep.SupportMove : Keep.No;
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
        if (fighter == 0 || s_finder is not { } finder || !ObjectHeader.TryRead(finder.Memory, fighter, out var header)
            || !ObjectFinder.Inherits(finder.Memory, header.ClassPrivate, s_fighterClass))
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

    /// <summary>A fighter's UMvsFollowerComponentBase, or 0.</summary>
    private static nint FollowerComponent(nint fighter) =>
        IsFighterObject(fighter) ? Read(Read(fighter, PawnComponentCache), ComponentCacheFollower) : 0;

    private static bool IsFighterObject(nint obj) =>
        obj != 0 && s_finder is { } finder && ObjectHeader.TryRead(finder.Memory, obj, out var header)
        && ObjectFinder.Inherits(finder.Memory, header.ClassPrivate, s_fighterClass);

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
        return $"{ClassName(fighter)} puppet(state{puppetState} driver={ClassName(Read(Read(puppet, PuppetDriver), Mvs.ActorComponentOwner))} victims={victims}) "
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
