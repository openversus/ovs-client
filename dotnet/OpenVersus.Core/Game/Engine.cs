namespace OpenVersus.Game;

/// <summary>
/// Whether the engine may be called at all. The plugin loads before the game has run any of its
/// own initialization, and calling into it then, even to look up a name, takes the process down
/// (netstats did exactly that on 2026-09-24). "Up" means the UFighterGameInstance has existed
/// for <see cref="SettleMs"/>: the instance is a UObject with a name, so the name table and the
/// object system are live by then, and the game constructs several instances in its first seconds,
/// so the settle time waits out the churn. Every function that calls the engine checks this
/// through <see cref="Require"/>; background work waits with <see cref="WaitUntilUp"/>.
/// </summary>
public static class Engine
{
    /// <summary>How long the game instance must have existed, in milliseconds, before the engine counts as up.</summary>
    public const int SettleMs = 3000;

    /// <summary>Tests replace the readiness check; null in the plugin.</summary>
    internal static Func<bool>? ReadyOverride { get; set; }

    /// <summary>True once a UFighterGameInstance has existed for <see cref="SettleMs"/>.</summary>
    public static bool IsUp => ReadyOverride?.Invoke() ?? (GameUi.FighterGameInstance != 0 && Environment.TickCount64 - GameUi.FighterGameInstanceTick >= SettleMs);

    /// <summary>Throws unless the engine is up, naming the caller, so a call made too early is a logged refusal instead of a crash.</summary>
    public static void Require(string caller)
    {
        if (!IsUp)
        {
            throw new InvalidOperationException($"{caller}: the engine is not up yet (no game instance for {SettleMs} ms); nothing may call into it");
        }
    }

    /// <summary>Blocks until the engine is up; false if <paramref name="stopping"/> was signaled first.</summary>
    public static bool WaitUntilUp(CancellationToken stopping, int pollMs = 500)
    {
        while (!IsUp)
        {
            if (stopping.WaitHandle.WaitOne(pollMs))
            {
                return false;
            }
        }

        return true;
    }
}
