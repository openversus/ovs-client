using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenVersus.Hooking;
using OpenVersus.Native;

namespace OpenVersus.Game;

/// <summary>
/// Runs work on the game thread. ProcessEvent, FText construction and the notification
/// manager are only safe there; a background thread queues an action and sets a Win32 timer
/// on the game's window, whose message pump then calls back on the game thread. The C++
/// poller used the same trick with one pending slot; this is a queue.
/// </summary>
public static unsafe class GameThread
{
	private const nuint TimerId = 0x0F5B;
	private static readonly ConcurrentQueue<(string Name, Action Work)> s_queue = new();
	private static Log? s_log;

	public static void Attach(Log log) => s_log = log;

	public static nint Window => User32.FindWindow("UnrealWindow", null);

	/// <summary>Queues <paramref name="work"/>; false if the game window does not exist yet.</summary>
	public static bool Post(string name, Action work)
	{
		nint window = Window;
		if (window == 0)
		{
			s_log?.Warn($"{name}: UnrealWindow not found; cannot run on the game thread");
			return false;
		}
		s_queue.Enqueue((name, work));
		if (User32.SetTimer(window, TimerId, 50, (nint)(delegate* unmanaged<nint, uint, nuint, uint, void>)&TimerProc) == 0)
		{
			s_log?.Warn($"{name}: SetTimer failed ({Marshal.GetLastPInvokeError()})");
			return false;
		}
		return true;
	}

	[UnmanagedCallersOnly]
	private static void TimerProc(nint window, uint message, nuint id, uint time)
	{
		User32.KillTimer(window, id);
		while (s_queue.TryDequeue(out var item))
			HookGuard.Run("GameThread:" + item.Name, item.Work, static work => work());
	}
}
