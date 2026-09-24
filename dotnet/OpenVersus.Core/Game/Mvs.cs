using System.Runtime.InteropServices;

namespace OpenVersus.Game;

/// <summary>Layouts and offsets in the game's own classes, from the C++ client's mvs.h.</summary>
public static class Mvs
{
    // UMvsDialog: the multicast delegates a dialog fires, after 0x3D8 bytes of base class.
    public const int DialogOnButtonOneClicked = 0x3E8;
    public const int DialogOnButtonTwoClicked = 0x410;
    public const int DialogOnButtonThreeClicked = 0x438;
    public const int DialogOnCancelled = 0x450; // spelled as the game spells it
    public const int DialogNativeOnDismissed = 0x478;

    // UMvsFrontendManager
    public const int FrontendCurrentStateWidget = 0xC8;

    // UMvsNotificationManager: the default widget class a notification needs to be visible.
    public const int NotificationManagerDefaultWidgetClass = 0xA0;

    // UPfgNetcodeSession, from the netstats handoff (final build).
    public const int SessionState = 0x5F1;          // uint8: 4 playing, 6 failed, 7/8 ended locally, 9 disconnecting
    public const int SessionPlayerIndex = 0x620;
    public const int SessionInputDelay = 0x628;
    public const int SessionResimCount = 0x63C;
    public const int SessionPing = 0x658;
    public const int SessionPacketLossPercent = 0x65C; // float
    public const int SessionRift = 0x660;              // float
    public const int SessionNumPredictedOverrides = 0x66C;
    public const int SessionNumZeroedOverrides = 0x670;
    public const int SessionStateManager = 0x710;      // pointer; its +0x420 is the current frame
    public const int StateManagerCurrentFrame = 0x420;
    // AMvsPreMatchGameState: its state machine, and the machine's current state.
    public const int PreMatchStateMachine = 0x320;
    public const int StateMachineCurrentState = 0x58;
    // APfgFixedPawn: which player a pawn is (CXXHeaderDump; read 0 and 1 off the two pawns of a
    // live 1v1 on 2026-09-24).
    public const int PawnPlayerIndex = 0x354;
    // AMvsFixedCharacter (every fighter pawn): the FGameplayPlayerData it was spawned with.
    // CXXHeaderDump, confirmed on the live game 2026-09-24: both pawns read the two players'
    // account ids, usernames, teams and indexes, and PlayerIndex here agreed with PawnPlayerIndex.
    // The MatchPlayerData_C objects the dump also offers do not exist during a match (only the
    // class default object does), and remote pawns have no player state; this struct is the source.
    public const int FixedCharacterGameplayPlayerData = 0x4E0;
    public const int GameplayPlayerDataAccountId = 0x00;   // FString
    public const int GameplayPlayerDataUsername = 0x10;    // FString
    public const int GameplayPlayerDataTeamIndex = 0x98;
    public const int GameplayPlayerDataPlayerIndex = 0x9C;
    public const int GameplayPlayerDataSize = 0x158;
    // UMvsGameplayConfig (a game-instance subsystem): the match the client was sent into.
    // CXXHeaderDump, confirmed live 2026-09-24: a 24-hex-digit id, the cluster name, and the
    // same two players at +0x70 (TArray<FGameplayPlayerData>).
    public const int GameplayConfigContainerMatchId = 0x50; // FString
    public const int GameplayConfigCluster = 0x60;          // FString
    public const int GameplayConfigPlayers = 0x70;          // TArray<FGameplayPlayerData>
    // APfgFixedGameStateBase
    public const int GameStateNetcodeSession = 0x4A0;
    public const int GameStateResimFrames = 0x524;

    // UObject header, as the C++ poller read it.
    public const int ObjectVTable = 0x00;
    /// <summary>
    /// int32, the object's slot in the object array. UE4SS MemberVariableLayout says 0xC, and the
    /// live check of 2026-09-24 read 0 and 65536 there for the first objects of chunks 0 and 1
    /// (structs/FUObjectArray.md in the RE archive), which is what a liveness check needs.
    /// </summary>
    public const int ObjectInternalIndex = 0x0C;
    public const int ObjectClassPrivate = 0x18;
    public const int ObjectNamePrivate = 0x20;
    public const int ObjectOuterPrivate = 0x28;
    public const int StructSuperStruct = 0x50;
    // UFunction vtables in the final build: native and blueprint-generated.
    public const uint UFunctionVTableNativeRva = 0x6795E20;
    public const uint UFunctionVTableBlueprintRva = 0x6795A90;
}

public enum EMvsNotificationCategory : byte
{
    None = 0, Social, Reward, System, Matchmaking, Toasts, Debug, MatchmakingErrors, LobbyInvites,
}

[StructLayout(LayoutKind.Sequential)]
public struct DialogFocusButton
{
    public int Value;
    [MarshalAs(UnmanagedType.U1)] public bool IsSet;
}

/// <summary>FMvsDialogParameters, 144 bytes. Every FText must be initialized with UE.EmptyText().</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FMvsDialogParameters
{
    public FText PromptText;
    public FText PromptDescriptionText;
    public nint DialogContentClass;
    public FText ButtonOneText;
    public FText ButtonTwoText;
    public FText ButtonThreeText;
    [MarshalAs(UnmanagedType.U1)] public bool ShowSpinner;
    [MarshalAs(UnmanagedType.U1)] public bool ShowExitButton;
    [MarshalAs(UnmanagedType.U1)] public bool ShowSolidBackground;
    [MarshalAs(UnmanagedType.U1)] public bool HideActionBar;
    public DialogFocusButton ButtonToFocus;

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
    public FText Text;
    public FText Caption;
    public fixed byte Icon[0x30];
    public float TimeoutSeconds;
    public uint Pad;
    public fixed byte Action[0x10];
    public FText ActionButtonText;
    public ulong WidgetClass;
    public EMvsNotificationCategory Category;
    [MarshalAs(UnmanagedType.U1)] public bool AutoDismissWhenClicked;
    public fixed byte Pad2[6];
    public nint UserData;

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
