using System.Runtime.InteropServices;

namespace OpenVersus.Game;

/// <summary>Layouts and offsets in the game's own classes, from the C++ client's mvs.h.</summary>
public static class Mvs
{
	// UMvsDialog: the multicast delegates a dialog fires, after 0x3D8 bytes of base class.
	public const int DialogOnButtonOneClicked = 0x3E8;
	public const int DialogOnButtonTwoClicked = 0x410;
	public const int DialogOnButtonThreeClicked = 0x438;
	public const int DialogOnCancelled = 0x450;
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
	// APfgFixedGameStateBase
	public const int GameStateNetcodeSession = 0x4A0;
	public const int GameStateResimFrames = 0x524;

	// UObject header, as the C++ poller read it.
	public const int ObjectVTable = 0x00;
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

/// <summary>FMvsDialogParameters, 144 bytes. Every FText must be initialised with UE.EmptyText().</summary>
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
			PromptText = empty, PromptDescriptionText = empty,
			ButtonOneText = empty, ButtonTwoText = empty, ButtonThreeText = empty,
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
			Text = empty, Caption = empty, ActionButtonText = empty,
			TimeoutSeconds = 5.0f, AutoDismissWhenClicked = true,
		};
	}
}
