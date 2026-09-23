using System.Runtime.InteropServices;
using OpenVersus.Config;
using OpenVersus.Game;
using OpenVersus.Hooking;

namespace OpenVersus.Hooks;

/// <summary>
/// The one-time "free mod" dialog and the "OpenVersus Loaded" toast. The C++ showed them from
/// inside the sunset checker, retrying on every call until the frontend was ready; here a
/// game-thread job retries once a second until both have shown.
/// </summary>
public static unsafe class StartupNotices
{
	private static State? s_state;
	private static Log? s_log;
	private static bool s_dialogDone;
	private static bool s_toastDone;
	private static int s_attempts;
	/// <summary>Once a second for ten minutes, then the notices are given up on and the log says so.</summary>
	private const int MaxAttempts = 600;

	/// <summary>Waits off the game thread for the game instance and window, then runs the job on the game thread.</summary>
	public static void Run(State state, Log log)
	{
		s_state = state;
		s_log = log;
		s_dialogDone = state.PaidModWarned;
		while (GameUi.FighterGameInstance == 0 || GameThread.Window == 0)
			Thread.Sleep(500);
		GameThread.Post("StartupNotices", Attempt, retryMs: 1000);
	}

	private static bool Attempt()
	{
		if (++s_attempts > MaxAttempts)
		{
			s_log?.Warn($"startup notices given up after {MaxAttempts} attempts (dialog {(s_dialogDone ? "shown" : "not shown")}, toast {(s_toastDone ? "shown" : "not shown")})");
			return true;
		}
		if (!s_dialogDone)
		{
			nint dialog = GameUi.ShowDialog("OpenVersus is a FREE mod!", "If you have paid for this, please ask for a refund!", "I Agree", "I Disagree");
			if (dialog != 0)
			{
				GameUi.AssignCallbackToButton(dialog, Mvs.DialogOnButtonOneClicked, (nint)(delegate* unmanaged<nint, void>)&SaveModWarningState);
				GameUi.AssignCallbackToButton(dialog, Mvs.DialogOnButtonTwoClicked, GameFunctions.Address(GameUi.QuitGameName));
				s_dialogDone = true;
			}
		}
		if (!s_toastDone && GameUi.ShowNotification("OpenVersus Loaded", OvsVersion.Current, 5.0f) != 0)
			s_toastDone = true;
		bool done = s_dialogDone && s_toastDone;
		if (done)
			s_log?.Info($"startup notices shown after {s_attempts} attempt(s)");
		return done;
	}

	[UnmanagedCallersOnly]
	private static void SaveModWarningState(nint delegateObject) => HookGuard.Run("SaveModWarningState", 0, static _ =>
	{
		s_state?.MarkPaidModWarned();
		s_log?.Info("Free-mod notice acknowledged");
	});
}
