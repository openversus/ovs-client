using System.Runtime.InteropServices;
using OpenVersus.Config;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// Bypasses the game's death date. A literal port of the C++ PatchSunsetSetterIntoOVSChecker:
/// the same two call sites go to the same checker, and the same bytes are written around them.
/// The checker also fires the one-time "free mod" dialog and the version toast, because it is
/// the first hook that runs once the game instance exists.
/// </summary>
public static unsafe class SunsetHook
{
	public const string InitThreadHeaderName = "Init_thread_header";
	public const string InitThreadFooterName = "Init_thread_footer";
	public const string FDateTimeName = "FDateTime::FDateTime";
	public const string SunsetDateName = "kSunsetDate";

	private static delegate* unmanaged<nint, void> s_initThreadHeader;
	private static delegate* unmanaged<nint, int> s_initThreadFooter;
	private static State? s_state;
	private static Log? s_log;
	private static bool s_paidWarningShown;
	private static bool s_versionShown;
	private static int s_calls;

	/// <summary>How many times the game has called the checker; the log reports it at shutdown.</summary>
	public static int Calls => s_calls;

	public static bool Apply(HookContext c)
	{
		s_log = c.Log;
		s_state = c.State;
		s_paidWarningShown = c.State.PaidModWarned;
		c.Log.Info("==Override Sunset Function==");
		var hit = c.Patterns.Find("SunsetDate", c.Settings.Pattern("pSunsetDate"));
		if (!hit.Found) return false;
		nint p = hit.Address;
		nint hook = (nint)(delegate* unmanaged<nint, byte>)&OfflineModeChecker;

		nint header = CallSite.Redirect(p + 7, hook, out string? error);
		if (error != null) { c.Log.Error($"SunsetDate: {error}"); return false; }
		s_initThreadHeader = (delegate* unmanaged<nint, void>)header;
		GameFunctions.Register(InitThreadHeaderName, header, FunctionSource.CallSite, "call at SunsetDate+7", "void Init_thread_header(int* tss)", c.Image);
		var footer = GameFunctions.FromCallSite(InitThreadFooterName, p + 0x47, "call at SunsetDate+47", "int Init_thread_footer(int* tss)", c.Image);
		s_initThreadFooter = (delegate* unmanaged<nint, int>)footer.Address;
		GameFunctions.FromCallSite(FDateTimeName, p + 0x3B, "call at SunsetDate+3B", "void FDateTime::FDateTime(uint64_t* this, int y, int m, int d, int h, int min, int s, int ms)", c.Image);
		nint sunsetDate = CallSite.Destination(p + 0x19, displacementOffset: 3, instructionLength: 7);
		GameFunctions.Register(SunsetDateName, sunsetDate, FunctionSource.CallSite, "lea at SunsetDate+19", "uint64_t kSunsetDate (data)", c.Image);

		nint finalBool = p - 0x2F;
		if ((error = CallSite.Inject(finalBool, hook, jump: false, out byte[] overwritten)) != null) { c.Log.Error($"SunsetDate: {error}"); return false; }
		c.Log.Debug($"SunsetDate: replaced {Log.Hex(overwritten)} at 0x{finalBool:X} with a call to the checker");
		if ((error = Write(finalBool + 5, [0xEB, 0x10])) != null) { c.Log.Error($"SunsetDate: {error}"); return false; }

		if ((error = Write(p + 0xC, [0xEB, 0xDA])) != null) { c.Log.Error($"SunsetDate: {error}"); return false; }
		nint nop = p + 0xE;
		for (int i = 0; i < 0x40 / 9 - 1; i++, nop += 9)
			if ((error = Write(nop, [0x66, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00])) != null) { c.Log.Error($"SunsetDate: {error}"); return false; }
		for (int i = 0; i < 12 / 4; i++, nop += 4)
			if ((error = Write(nop, [0x0F, 0x1F, 0x40, 0x00])) != null) { c.Log.Error($"SunsetDate: {error}"); return false; }
		nop = finalBool + 7;
		for (int i = 0; i < 16 / 4; i++, nop += 4)
			if ((error = Write(nop, [0x0F, 0x1F, 0x40, 0x00])) != null) { c.Log.Error($"SunsetDate: {error}"); return false; }

		foreach (string name in new[] { InitThreadHeaderName, InitThreadFooterName, FDateTimeName, SunsetDateName })
			c.Log.Debug(GameFunctions.Find(name)!.ToString());
		c.Log.Success("Sunset Function Proxied");
		return true;
	}

	private static string? Write(nint at, ReadOnlySpan<byte> bytes) => CodeWriter.Write(at, bytes, code: true);

	[UnmanagedCallersOnly]
	private static byte OfflineModeChecker(nint tss) => HookGuard.Run("OfflineModeChecker", tss, static tss =>
	{
		int call = Interlocked.Increment(ref s_calls);
		if (call <= 10 || call % 100 == 0)
			s_log?.Debug($"sunset checker call #{call} (tss 0x{tss:X}, thread {Environment.CurrentManagedThreadId})");
		s_initThreadHeader(tss);
		if (*(int*)tss == -1)
			s_initThreadFooter(tss);

		if (!s_paidWarningShown && GameUi.FighterGameInstance != 0)
		{
			nint dialog = GameUi.ShowDialog("OpenVersus is a FREE mod!", "If you have paid for this, please ask for a refund!", "I Agree", "I Disagree");
			if (dialog != 0)
			{
				GameUi.AssignCallbackToButton(dialog, Mvs.DialogOnButtonOneClicked, (nint)(delegate* unmanaged<nint, void>)&SaveModWarningState);
				GameUi.AssignCallbackToButton(dialog, Mvs.DialogOnButtonTwoClicked, GameFunctions.Address(GameUi.QuitGameName));
				s_paidWarningShown = true;
			}
		}
		if (!s_versionShown && GameUi.FighterGameInstance != 0)
		{
			if (GameUi.ShowNotification("OpenVersus Loaded", OvsVersion.Current, 5.0f) != 0)
				s_versionShown = true;
		}
		return (byte)0;
	}, (byte)0);

	[UnmanagedCallersOnly]
	private static void SaveModWarningState(nint delegateObject) => HookGuard.Run("SaveModWarningState", 0, static _ =>
	{
		s_state?.MarkPaidModWarned();
		s_log?.Info("Free-mod notice acknowledged");
	});
}
