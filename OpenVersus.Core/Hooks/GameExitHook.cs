using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using OpenVersus.Net;

namespace OpenVersus.Hooks;

/// <summary>
/// The engine's own way out: FWindowsPlatformMisc::RequestExit(bool Force). With Force false it asks the engine to exit
/// (RequestEngineExit("Win RequestExit")) and posts WM_QUIT, which is where the game's quit dialog and the console's
/// quit command end up; the engine then shuts down as it does from the menu (session closed, settings saved). With Force
/// true it ends in TerminateProcess, which is what the updater did by hand before this. Found by its prologue, and
/// hooked at its entry: every exit the game asks for itself (Alt+F4 and the window's close button arrive here through
/// the window-close handler, the menu's quit through the console command) is reported to the server first
/// (<see cref="Report"/>), which decides whether it was a match walked out on; the mod's own request is not reported.
/// </summary>
public static unsafe class GameExitHook
{
    /// <summary>The <see cref="GameFunctions"/> key.</summary>
    public const string RequestExitName = "RequestExit";

    /// <summary>The server route a quit is reported to.</summary>
    public const string ReportPath = "/ovs/quit";

    /// <summary>How long the report may take: the exit waits for it, on the game thread.</summary>
    public static readonly TimeSpan ReportTimeout = TimeSpan.FromMilliseconds(1500);

    // sub rsp, 0x28; test cl, cl: moved whole into the gateway (the je after them reads the flags test leaves, which the
    // gateway's jump back does not touch).
    private static readonly byte[] s_prologue = [0x48, 0x83, 0xEC, 0x28, 0x84, 0xC9];
    private static delegate* unmanaged<byte, void> s_original;
    private static volatile bool s_own;
    private static string s_reportUrl = "";
    private static IHttpTransport? s_http;
    private static ILogger? s_log;

    /// <summary>
    /// The function's first instructions: sub rsp; test cl, cl; je (the Force branch); the GIsCriticalError-style byte
    /// read, neg, sbb, and 3; add rsp; jmp to the status variant; then the reason string's lea and the call. Steam's
    /// build: 0x02BCD0A0.
    /// </summary>
    internal const string Pattern = "48 83 EC 28 84 C9 74 17 0F B6 05 ? ? ? ? F6 D8 1A D2 80 E2 03 48 83 C4 28 E9 ? ? ? ? 48 8D 0D ? ? ? ? E8";

    /// <summary>Resolves the function; false when the pattern is not found (the updater then ends the process as before).</summary>
    public static bool Apply(HookContext c)
    {
        var hit = c.Patterns.Find("RequestExit", Pattern);
        if (!hit.Found)
        {
            return false;
        }

        GameFunctions.Register(RequestExitName, hit.Address, FunctionSource.Pattern, "RequestExit", "void FWindowsPlatformMisc::RequestExit(bool Force)", c.Image);
        c.Log.Debug(GameFunctions.Find(RequestExitName)!.ToString());
        s_log = c.Log;
        try
        {
            s_original = (delegate* unmanaged<byte, void>)EntryHook.Install(hit.Address, s_prologue, (nint)(delegate* unmanaged<byte, void>)&OnRequestExit);
            c.Log.Success($"Game exit hooked at 0x{hit.Address:X}: a quit the game asks for is reported to the server first");
        }
        catch (PatchException e)
        {
            c.Log.Warn($"Game exit: RequestExit could not be hooked ({e.Message}); quits are not reported");
        }

        return true;
    }

    /// <summary>Where quits are reported: the server and the transport that carries the client's identity. Nothing is reported until this is called.</summary>
    public static void Attach(string serverUrl, IHttpTransport http)
    {
        s_reportUrl = Urls.Join(serverUrl, ReportPath)?.ToString() ?? "";
        s_http = http;
    }

    [UnmanagedCallersOnly]
    private static void OnRequestExit(byte force)
    {
        HookGuard.Run("RequestExit", force, static f =>
        {
            if (!s_own && s_http is { } http && s_reportUrl.Length > 0)
            {
                Report(http, s_reportUrl, f != 0, s_log);
            }
        });
        s_original(force);
    }

    /// <summary>
    /// One POST to <paramref name="url"/>, waited for up to <see cref="ReportTimeout"/>: the game is on its way out, and a
    /// report that leaves after the process does is no report. The answer is logged and nothing else is done with it.
    /// </summary>
    public static bool Report(IHttpTransport http, string url, bool forced, ILogger? log)
    {
        string body = $$"""{"forced":{{(forced ? "true" : "false")}}}""";
        var result = http.Post(new Uri(url), "application/json", Encoding.UTF8.GetBytes(body), ReportTimeout);
        if (result.Ok)
        {
            log?.Info($"[Exit] The game asked to exit{(forced ? " (forced)" : "")}; reported to the server ({Compact(result.Text)})");
        }
        else
        {
            log?.Warn($"[Exit] The game asked to exit{(forced ? " (forced)" : "")}; the report did not land ({result.Error ?? $"HTTP {result.Status}"})");
        }

        return result.Ok;
    }

    private static string Compact(string text) => text.Length > 200 ? text[..200] + "..." : text.ReplaceLineEndings(" ");

    /// <summary>
    /// Asks the engine to exit, on the game thread (WM_QUIT goes to the caller's queue). False, with the reason logged, when
    /// the function is not resolved or the game window is not there to post to: the caller ends the process instead.
    /// </summary>
    public static bool Request(ILogger log)
    {
        nint function = GameFunctions.Address(RequestExitName);
        if (function == 0)
        {
            log.Warn("[Exit] RequestExit is not resolved; the process is ended instead");
            return false;
        }

        bool posted = GameThread.Post("RequestExit", () =>
        {
            log.Info("[Exit] Asking the engine to exit (FPlatformMisc::RequestExit(false))");
            s_own = true; // the mod's own exit: not a quit to report
            ((delegate* unmanaged<byte, void>)function)(0);
        });
        if (!posted)
        {
            log.Warn("[Exit] The game thread cannot be reached; the process is ended instead");
        }

        return posted;
    }
}
