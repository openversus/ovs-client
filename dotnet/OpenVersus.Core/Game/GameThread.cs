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
	private static readonly ConcurrentQueue<(string Name, Func<bool> Work, int RetryMs)> s_queue = new();
	private static Log? s_log;

	public static void Attach(Log log) => s_log = log;

	public static nint Window => User32.FindWindow("UnrealWindow", null);

	/// <summary>Queues <paramref name="work"/> to run once; false if the game window does not exist yet.</summary>
	public static bool Post(string name, Action work) => Post(name, () => { work(); return true; }, 0);

	/// <summary>
	/// Queues <paramref name="work"/>, which returns true when it is finished; until then it is
	/// run again every <paramref name="retryMs"/>. False if the game window does not exist yet.
	/// </summary>
	public static bool Post(string name, Func<bool> work, int retryMs) => Post(name, work, retryMs, 50);

	private static bool Post(string name, Func<bool> work, int retryMs, int delayMs)
	{
		nint window = Window;
		if (window == 0)
		{
			s_log?.Warn($"{name}: UnrealWindow not found; cannot run on the game thread");
			return false;
		}
		s_queue.Enqueue((name, work, retryMs));
		s_log?.Trace($"game thread: queued {name} (delay {delayMs} ms, {s_queue.Count} queued) from thread {Environment.CurrentManagedThreadId}");
		if (User32.SetTimer(window, TimerId, (uint)Math.Max(delayMs, 1), (nint)(delegate* unmanaged<nint, uint, nuint, uint, void>)&TimerProc) == 0)
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
		int pending = s_queue.Count;
		s_log?.Trace($"game thread: timer fired on thread {Environment.CurrentManagedThreadId}, {pending} queued");
		for (int i = 0; i < pending && s_queue.TryDequeue(out var item); i++)
		{
			bool done = HookGuard.Run("GameThread:" + item.Name, item.Work, static work => work(), true);
			if (!done)
				Post(item.Name, item.Work, item.RetryMs, item.RetryMs);
		}
	}
}
