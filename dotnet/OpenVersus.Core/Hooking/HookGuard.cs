namespace OpenVersus.Hooking;

/// <summary>
/// Runs a hook body so that nothing escapes into the game. An exception leaving an
/// UnmanagedCallersOnly method takes the process down, so every hook is a one-line stub that
/// hands its arguments here as a value, with a static lambda, and gets a fallback back on
/// failure. Static lambdas and value-typed state keep the hook path free of allocations.
/// The first few failures of each hook are logged; after that they are only counted, so a
/// hook that fails every frame cannot fill the disk.
/// </summary>
public static class HookGuard
{
    private const int LoggedFailuresPerHook = 5;
    private static readonly Dictionary<string, int> s_failures = new();
    private static readonly object s_lock = new();
    private static Log? s_log;

    public static void Attach(Log log) => s_log = log;

    public static TResult Run<TState, TResult>(string hook, TState state, Func<TState, TResult> body, TResult fallback)
    {
        try
        {
            return body(state);
        }
        catch (Exception e)
        {
            Report(hook, e);
            return fallback;
        }
    }

    public static void Run<TState>(string hook, TState state, Action<TState> body)
    {
        try
        {
            body(state);
        }
        catch (Exception e)
        {
            Report(hook, e);
        }
    }

    /// <summary>How many times each hook has failed so far, for the shutdown summary.</summary>
    public static IReadOnlyDictionary<string, int> Failures
    {
        get
        {
            lock (s_lock)
            {
                return new Dictionary<string, int>(s_failures);
            }
        }
    }

    private static void Report(string hook, Exception e)
    {
        int count;
        lock (s_lock)
        {
            s_failures.TryGetValue(hook, out count);
            s_failures[hook] = ++count;
        }
        if (count <= LoggedFailuresPerHook)
        {
            s_log?.Error($"hook {hook} failed ({count}): {e}");
        }
        else if (count == LoggedFailuresPerHook + 1)
        {
            s_log?.Error($"hook {hook} keeps failing; further failures are counted, not logged");
        }
    }
}
