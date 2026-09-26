using System.Runtime.InteropServices;
using OpenVersus.Config;
using OpenVersus.Game;
using OpenVersus.Hooking;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// The one-time "free mod" dialog, the "OpenVersus Loaded" toast, and a second toast when the
/// settings could not be used (<see cref="Settings.Problem"/>). The C++ showed them from
/// inside the sunset checker, retrying on every call until the frontend was ready; here a
/// game-thread job retries once a second until both have shown.
/// </summary>
public static unsafe class StartupNotices
{
    private static State? s_state;
    private static ILogger? s_log;
    private static bool s_dialogDone;
    private static bool s_toastDone;
    private static SettingsProblem? s_problem;
    private static bool s_problemDone;
    private static int s_attempts;
    /// <summary>Once a second for ten minutes, then the notices are given up on and the log says so.</summary>
    private const int MaxAttempts = 600;

    /// <summary>Waits off the game thread for the game instance and window, then runs the job on the game thread.</summary>
    public static void Run(State state, SettingsProblem? problem, ILogger log)
    {
        s_state = state;
        s_log = log;
        s_dialogDone = state.PaidModWarned;
        s_problem = problem;
        s_problemDone = problem == null;
        log.Debug("startup notices: waiting for the game instance and window");
        while (GameUi.FighterGameInstance == 0 || GameThread.Window == 0)
        {
            Thread.Sleep(500);
        }

        log.Debug($"startup notices: instance 0x{GameUi.FighterGameInstance:X}, window 0x{GameThread.Window:X}; posting to the game thread");
        if (!GameThread.Post("StartupNotices", Attempt, retryMs: 1000))
        {
            log.Warn("startup notices: could not post to the game thread");
        }
    }


    private static bool Attempt()
    {
        if (++s_attempts > MaxAttempts)
        {
            s_log?.Warn($"startup notices given up after {MaxAttempts} attempts (dialog {(s_dialogDone ? "shown" : "not shown")}, toast {(s_toastDone ? "shown" : "not shown")}, settings toast {(s_problemDone ? "shown or not needed" : "not shown")})");
            return true;
        }
        bool verbose = s_attempts <= 5 || s_attempts % 30 == 0;

        // The game constructs several instances in its first seconds, and a native exception
        // out of the UI unwinds through our frames without a trace. So: no game call until the
        // instance has been stable for a while and the frontend has a current state widget,
        // which is what the C++ dialog path required.
        long age = Environment.TickCount64 - GameUi.FighterGameInstanceTick;
        if (!Engine.IsUp)
        {
            if (verbose)
            {
                s_log?.Trace($"startup notices attempt {s_attempts}: instance 0x{GameUi.FighterGameInstance:X} is {age} ms old; waiting");
            }

            return false;
        }
        var (frontend, stateWidget) = GameUi.FrontendState();
        if (frontend == 0 || stateWidget == 0)
        {
            if (verbose)
            {
                s_log?.Trace($"startup notices attempt {s_attempts}: frontend 0x{frontend:X}, state widget 0x{stateWidget:X}; waiting");
            }

            return false;
        }

        if (!s_dialogDone)
        {
            s_log?.Debug($"startup notices attempt {s_attempts}: showing the free-mod dialog");
            nint dialog = GameUi.ShowDialog("OpenVersus is a FREE mod!", "If you paid someone for these files, you should ask them for a refund!", "OK");
            s_log?.Debug($"startup notices: dialog 0x{dialog:X}");
            if (dialog != 0)
            {
                GameUi.AssignCallbackToButton(dialog, Mvs.DialogOnButtonOneClicked, (nint)(delegate* unmanaged<nint, void>)&SaveModWarningState);
                s_dialogDone = true;
            }
        }
        bool toastThisAttempt = false;
        if (!s_toastDone)
        {
            nint manager = GameUi.NotificationManager();
            nint widgetClass = 0;
            if (manager != 0)
            {
                Memory.CodeWriter.TryRead(manager + Mvs.NotificationManagerDefaultWidgetClass, out widgetClass);
            }

            s_log?.Debug($"startup notices attempt {s_attempts}: manager 0x{manager:X}, widget class 0x{widgetClass:X}; requesting the toast");
            if (manager != 0 && widgetClass != 0)
            {
                nint shown = GameUi.ShowNotification("OpenVersus Loaded", OvsVersion.Current, 5.0f, setWidgetClass: true);
                s_log?.Debug($"startup notices: toast request returned 0x{shown:X}");
                if (shown != 0)
                {
                    s_toastDone = true;
                    toastThisAttempt = true;
                }
            }
        }
        // An attempt after the loaded toast, so the two do not ask the manager in the same frame.
        if (s_toastDone && !toastThisAttempt && !s_problemDone && s_problem != null)
        {
            nint shown = GameUi.ShowNotification(s_problem.Title, s_problem.Detail, 15.0f, setWidgetClass: true);
            s_log?.Debug($"startup notices: settings toast request returned 0x{shown:X}");
            s_problemDone = shown != 0;
            if (s_problemDone)
            {
                s_log?.Info($"startup notices: settings toast shown: {s_problem.Title} ({s_problem.Detail})");
            }

            return false;
        }

        bool done = s_dialogDone && s_toastDone && s_problemDone;
        if (done)
        {
            s_log?.Info($"startup notices shown after {s_attempts} attempt(s)");
        }

        return done;
    }

    [UnmanagedCallersOnly]
    private static void SaveModWarningState(nint delegateObject) => HookGuard.Run("SaveModWarningState", 0, static _ =>
    {
        s_state?.MarkPaidModWarned();
        s_log?.Info("Free-mod notice acknowledged");
    });
}
