using OpenVersus.Native;

namespace OpenVersus.Net;

/// <summary>
/// The small always-on-top window shown while a required update downloads at startup, before
/// the game has a window of its own. It runs its own thread and message loop, so a slow
/// connection never turns it into "Not responding"; the downloading thread only sets its text
/// and bar. It has no close button: the update finishes or fails on its own. Everything is
/// best effort: when the window cannot be made, the update goes on without it.
/// </summary>
public sealed unsafe class UpdateProgressWindow : IDisposable
{
    private const string ClassName = "OpenVersusUpdateProgress";
    private const int Width = 540, Height = 185;
    private static bool s_registered;

    private readonly long _totalBytes;
    private long _doneBytes;
    private int _shownPermille = -1;
    private uint _threadId;
    private nint _status, _bar;

    private UpdateProgressWindow(long totalBytes) => _totalBytes = Math.Max(1, totalBytes);

    /// <summary>Opens the window for a download of <paramref name="totalBytes"/>; returns once it is up (or has failed to come up).</summary>
    public static UpdateProgressWindow Open(long totalBytes, string heading)
    {
        var window = new UpdateProgressWindow(totalBytes);
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => window.Loop(heading, ready)) { IsBackground = true, Name = "OVS update window" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return window;
    }

    /// <summary>Shows which file is downloading.</summary>
    public void SetFile(string name, int number, int count) =>
        SetStatus($"Downloading {name} ({number} of {count}). Keep the game open.");

    /// <summary>Moves the bar on by <paramref name="bytes"/>.</summary>
    public void AddBytes(int bytes)
    {
        _doneBytes += bytes;
        int permille = (int)(Math.Min(_doneBytes, _totalBytes) * 1000 / _totalBytes);
        if (_bar != 0 && permille != _shownPermille)
        {
            _shownPermille = permille;
            User32.SendMessage(_bar, User32.PBM_SETPOS, (nuint)permille, 0);
        }
    }

    /// <summary>Shows <paramref name="text"/> under the heading.</summary>
    public void SetStatus(string text)
    {
        if (_status != 0)
        {
            User32.SetWindowText(_status, text);
        }
    }

    /// <summary>Closes the window.</summary>
    public void Dispose()
    {
        if (_threadId != 0)
        {
            User32.PostThreadMessage(_threadId, User32.WM_QUIT, 0, 0);
            _threadId = 0;
        }
    }

    private void Loop(string heading, ManualResetEventSlim ready)
    {
        nint window = 0;
        try
        {
            window = Create(heading);
            if (window != 0)
            {
                _threadId = Kernel32.GetCurrentThreadId();
            }
        }
        finally
        {
            ready.Set();
        }

        if (window == 0)
        {
            return;
        }

        while (User32.GetMessage(out var message, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(message);
            User32.DispatchMessage(message);
        }

        User32.DestroyWindow(window);
    }

    private nint Create(string heading)
    {
        nint instance = Kernel32.GetModuleHandle(null);
        Comctl32.InitCommonControlsEx(new Comctl32.InitCommonControls { Size = 8, Classes = Comctl32.ICC_PROGRESS_CLASS });
        if (!s_registered)
        {
            // DefWindowProcW itself is the window procedure: the window needs no behavior of its
            // own, and a managed callback would be one more thing that could fault on the game.
            nint user32 = Kernel32.GetModuleHandle("user32.dll");
            nint defWindowProc = user32 == 0 ? 0 : Kernel32.GetProcAddress(user32, "DefWindowProcW");
            if (defWindowProc == 0)
            {
                return 0;
            }

            fixed (char* name = ClassName)
            {
                var windowClass = new User32.WndClass
                {
                    WndProc = defWindowProc,
                    Instance = instance,
                    Cursor = User32.LoadCursor(0, User32.IDC_ARROW),
                    Background = 5 + 1, // COLOR_WINDOW + 1
                    ClassName = name,
                };
                // Registering twice in one process fails with "already exists", which is fine.
                User32.RegisterClass(windowClass);
            }

            s_registered = true;
        }

        int x = Math.Max(0, (User32.GetSystemMetrics(User32.SM_CXSCREEN) - Width) / 2);
        int y = Math.Max(0, (User32.GetSystemMetrics(User32.SM_CYSCREEN) - Height) / 2);
        nint window = User32.CreateWindowEx(User32.WS_EX_TOPMOST, ClassName, "OpenVersus update", User32.WS_CAPTION_ONLY, x, y, Width, Height, 0, 0, instance, 0);
        if (window == 0)
        {
            return 0;
        }

        nint font = Comctl32.GetStockObject(Comctl32.DEFAULT_GUI_FONT);
        nint title = User32.CreateWindowEx(0, "STATIC", heading, User32.WS_CHILD | User32.WS_VISIBLE, 22, 18, 490, 24, window, 0, instance, 0);
        _status = User32.CreateWindowEx(0, "STATIC", "Keep MultiVersus open until this finishes.", User32.WS_CHILD | User32.WS_VISIBLE, 22, 48, 490, 38, window, 0, instance, 0);
        _bar = User32.CreateWindowEx(0, Comctl32.PROGRESS_CLASS, null, User32.WS_CHILD | User32.WS_VISIBLE, 22, 94, 490, 24, window, 0, instance, 0);
        foreach (nint control in (ReadOnlySpan<nint>)[title, _status])
        {
            if (control != 0 && font != 0)
            {
                User32.SendMessage(control, User32.WM_SETFONT, (nuint)font, 1);
            }
        }

        if (_bar != 0)
        {
            User32.SendMessage(_bar, User32.PBM_SETRANGE32, 0, 1000);
        }

        User32.ShowWindow(window, User32.SW_SHOW);
        return window;
    }
}
