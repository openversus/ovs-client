using System.Runtime.InteropServices;

namespace OpenVersus.Game;

/// <summary>Layouts and offsets in the game's own classes, from the C++ client's mvs.h.</summary>
public static class Mvs
{
    // UMvsDialog: the multicast delegates a dialog fires, after 0x3D8 bytes of base class.
    /// <summary>The first button's clicked delegate (a <see cref="TMulticastDelegateBase"/>).</summary>
    public const int DialogOnButtonOneClicked = 0x3E8;
    /// <summary>The second button's clicked delegate.</summary>
    public const int DialogOnButtonTwoClicked = 0x410;
    /// <summary>The third button's clicked delegate.</summary>
    public const int DialogOnButtonThreeClicked = 0x438;
    /// <summary>The canceled delegate, spelled as the game spells it.</summary>
    public const int DialogOnCancelled = 0x450;
    /// <summary>The native dismissed delegate.</summary>
    public const int DialogNativeOnDismissed = 0x478;

    // UMvsFrontendManager
    /// <summary>The current state widget; the C++ client opened a dialog only once this was set.</summary>
    public const int FrontendCurrentStateWidget = 0xC8;

    // UMvsNotificationManager
    /// <summary>The default widget class, which a notification needs to be visible when it is asked for from outside the game's own flow.</summary>
    public const int NotificationManagerDefaultWidgetClass = 0xA0;

    // UPfgNetcodeSession, from the netstats handoff (final build).
    /// <summary>uint8: 4 playing, 6 failed, 7 or 8 ended locally, 9 disconnecting.</summary>
    public const int SessionState = 0x5F1;
    /// <summary>int32: the local player's index; -1 until the session is given one.</summary>
    public const int SessionPlayerIndex = 0x620;
    /// <summary>int32: the input delay. Constructed as 2; the game raises it from the reported ping and never lowers it.</summary>
    public const int SessionInputDelay = 0x628;
    /// <summary>int32: resimulations so far.</summary>
    public const int SessionResimCount = 0x63C;
    /// <summary>int32: the ping.</summary>
    public const int SessionPing = 0x658;
    /// <summary>float: the packet loss, in percent.</summary>
    public const int SessionPacketLossPercent = 0x65C;
    /// <summary>float: the rift.</summary>
    public const int SessionRift = 0x660;
    /// <summary>int32: predicted input overrides, as a running maximum: the highest figure the server has reported this match, not the latest (netcode/session.md in the RE archive).</summary>
    public const int SessionNumPredictedOverrides = 0x66C;
    /// <summary>int32: zeroed input overrides, a running maximum like <see cref="SessionNumPredictedOverrides"/>.</summary>
    public const int SessionNumZeroedOverrides = 0x670;
    /// <summary>Pointer to the session's state manager; its <see cref="StateManagerCurrentFrame"/> is the current frame.</summary>
    public const int SessionStateManager = 0x710;
    /// <summary>int32: the current frame, in the state manager.</summary>
    public const int StateManagerCurrentFrame = 0x420;
    // AMvsPreMatchGameState: its state machine, and the machine's current state.
    /// <summary>Pointer to the pre-match game state's state machine.</summary>
    public const int PreMatchStateMachine = 0x320;
    /// <summary>Pointer to the state machine's current state.</summary>
    public const int StateMachineCurrentState = 0x58;
    /// <summary>
    /// APfgFixedPawn, int32: which player a pawn is (CXXHeaderDump; read 0 and 1 off the two
    /// pawns of a live 1v1 on 2026-09-24).
    /// </summary>
    public const int PawnPlayerIndex = 0x354;
    /// <summary>
    /// AMvsFixedCharacter (every fighter pawn): the FGameplayPlayerData it was spawned with.
    /// CXXHeaderDump, confirmed on the live game 2026-09-24: both pawns read the two players'
    /// account ids, usernames, teams and indexes, and PlayerIndex here agreed with
    /// <see cref="PawnPlayerIndex"/>. The MatchPlayerData_C objects the dump also offers do not
    /// exist during a match (only the class default object does), and remote pawns have no
    /// player state; this struct is the source.
    /// </summary>
    public const int FixedCharacterGameplayPlayerData = 0x4E0;
    /// <summary>FGameplayPlayerData, FString: the account id.</summary>
    public const int GameplayPlayerDataAccountId = 0x00;
    /// <summary>FGameplayPlayerData, FString: the username.</summary>
    public const int GameplayPlayerDataUsername = 0x10;
    /// <summary>FGameplayPlayerData, int32: the team.</summary>
    public const int GameplayPlayerDataTeamIndex = 0x98;
    /// <summary>FGameplayPlayerData, int32: the player index.</summary>
    public const int GameplayPlayerDataPlayerIndex = 0x9C;
    /// <summary>The size of one FGameplayPlayerData, the stride of <see cref="GameplayConfigPlayers"/>.</summary>
    public const int GameplayPlayerDataSize = 0x158;
    // UMvsGameplayConfig (a game-instance subsystem): the match the client was sent into.
    // CXXHeaderDump, confirmed live 2026-09-24.
    /// <summary>FString: the match id, 24 hex digits.</summary>
    public const int GameplayConfigContainerMatchId = 0x50;
    /// <summary>FString: the cluster name.</summary>
    public const int GameplayConfigCluster = 0x60;
    /// <summary>TArray&lt;FGameplayPlayerData&gt;: the same players the pawns carry.</summary>
    public const int GameplayConfigPlayers = 0x70;
    // APfgFixedGameStateBase
    /// <summary>Pointer to the game state's UPfgNetcodeSession.</summary>
    public const int GameStateNetcodeSession = 0x4A0;
    /// <summary>uint32: the game state's resimulated frames.</summary>
    public const int GameStateResimFrames = 0x524;

    // UObject header, as the C++ poller read it.
    /// <summary>The vtable pointer.</summary>
    public const int ObjectVTable = 0x00;
    /// <summary>
    /// int32, the object's slot in the object array. UE4SS MemberVariableLayout says 0xC, and the
    /// live check of 2026-09-24 read 0 and 65536 there for the first objects of chunks 0 and 1
    /// (structs/FUObjectArray.md in the RE archive), which is what a liveness check needs.
    /// </summary>
    public const int ObjectInternalIndex = 0x0C;
    /// <summary>The object's class.</summary>
    public const int ObjectClassPrivate = 0x18;
    /// <summary>The object's FName.</summary>
    public const int ObjectNamePrivate = 0x20;
    /// <summary>The object's outer: the object it belongs to, such as a function's class.</summary>
    public const int ObjectOuterPrivate = 0x28;
    /// <summary>UStruct: the parent struct or class; 0 at the root.</summary>
    public const int StructSuperStruct = 0x50;
    // UFunction vtables in the final build: native and blueprint-generated.
    /// <summary>The RVA of a native UFunction's vtable, by which <see cref="ObjectFinder.FindFunction"/> tells a real UFunction from anything else with the same name.</summary>
    public const uint UFunctionVTableNativeRva = 0x6795E20;
    /// <summary>The RVA of a blueprint-generated UFunction's vtable; see <see cref="UFunctionVTableNativeRva"/>.</summary>
    public const uint UFunctionVTableBlueprintRva = 0x6795A90;
}

/// <summary>EMvsNotificationCategory: how the game files a notification.</summary>
public enum EMvsNotificationCategory : byte
{
    /// <summary>No category; what the client sends.</summary>
    None = 0,
    /// <summary>The game's Social category.</summary>
    Social,
    /// <summary>The game's Reward category.</summary>
    Reward,
    /// <summary>The game's System category.</summary>
    System,
    /// <summary>The game's Matchmaking category.</summary>
    Matchmaking,
    /// <summary>The game's Toasts category.</summary>
    Toasts,
    /// <summary>The game's Debug category.</summary>
    Debug,
    /// <summary>The game's MatchmakingErrors category.</summary>
    MatchmakingErrors,
    /// <summary>The game's LobbyInvites category.</summary>
    LobbyInvites,
}

/// <summary>Which button a dialog focuses when it opens; unset leaves the choice to the game.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DialogFocusButton
{
    /// <summary>The button's index.</summary>
    public int Value;
    /// <summary>Whether <see cref="Value"/> applies.</summary>
    [MarshalAs(UnmanagedType.U1)] public bool IsSet;
}

/// <summary>FMvsDialogParameters, 144 bytes. Every FText must be initialized with UE.EmptyText().</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FMvsDialogParameters
{
    /// <summary>The prompt: the dialog's main text.</summary>
    public FText PromptText;
    /// <summary>The description shown with the prompt.</summary>
    public FText PromptDescriptionText;
    /// <summary>A widget class for custom dialog content; left 0 here.</summary>
    public nint DialogContentClass;
    /// <summary>The first button's label.</summary>
    public FText ButtonOneText;
    /// <summary>The second button's label.</summary>
    public FText ButtonTwoText;
    /// <summary>The third button's label.</summary>
    public FText ButtonThreeText;
    /// <summary>Shows a busy spinner.</summary>
    [MarshalAs(UnmanagedType.U1)] public bool ShowSpinner;
    /// <summary>Shows the exit button.</summary>
    [MarshalAs(UnmanagedType.U1)] public bool ShowExitButton;
    /// <summary>Draws a solid background behind the dialog.</summary>
    [MarshalAs(UnmanagedType.U1)] public bool ShowSolidBackground;
    /// <summary>Hides the action bar.</summary>
    [MarshalAs(UnmanagedType.U1)] public bool HideActionBar;
    /// <summary>The button focused when the dialog opens.</summary>
    public DialogFocusButton ButtonToFocus;

    /// <summary>Every text empty, as the struct requires, the exit button shown and everything else off.</summary>
    public static FMvsDialogParameters Empty()
    {
        FText empty = UE.EmptyText();
        return new FMvsDialogParameters
        {
            PromptText = empty,
            PromptDescriptionText = empty,
            ButtonOneText = empty,
            ButtonTwoText = empty,
            ButtonThreeText = empty,
            ShowExitButton = true,
        };
    }
}

/// <summary>FMvsNotificationData, 168 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FMvsNotificationData
{
    /// <summary>The body text.</summary>
    public FText Text;
    /// <summary>The caption.</summary>
    public FText Caption;
    /// <summary>The icon: 0x30 opaque bytes, left zeroed.</summary>
    public fixed byte Icon[0x30];
    /// <summary>How long the notification stays up, in seconds.</summary>
    public float TimeoutSeconds;
    /// <summary>Padding.</summary>
    public uint Pad;
    /// <summary>The click action: 16 opaque bytes, left zeroed.</summary>
    public fixed byte Action[0x10];
    /// <summary>The action button's label.</summary>
    public FText ActionButtonText;
    /// <summary>
    /// The widget class to show it with. The manager drops a notification without one when it is asked
    /// for from outside the game's own flow; see <see cref="GameUi.ShowNotification"/>.
    /// </summary>
    public ulong WidgetClass;
    /// <summary>The category; the client sends <see cref="EMvsNotificationCategory.None"/>.</summary>
    public EMvsNotificationCategory Category;
    /// <summary>Dismisses the notification when it is clicked.</summary>
    [MarshalAs(UnmanagedType.U1)] public bool AutoDismissWhenClicked;
    /// <summary>Padding.</summary>
    public fixed byte Pad2[6];
    /// <summary>Opaque; left 0.</summary>
    public nint UserData;

    /// <summary>Every text empty, a five-second timeout, dismissed when clicked, everything else zero.</summary>
    public static FMvsNotificationData Empty()
    {
        FText empty = UE.EmptyText();
        return new FMvsNotificationData
        {
            Text = empty,
            Caption = empty,
            ActionButtonText = empty,
            TimeoutSeconds = 5.0f,
            AutoDismissWhenClicked = true,
        };
    }
}
