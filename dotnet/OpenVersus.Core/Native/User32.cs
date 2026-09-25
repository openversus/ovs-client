using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>The user32 functions and constants the client uses. Names follow the Windows SDK.</summary>
public static unsafe partial class User32
{
    /// <summary>MessageBox: the information icon.</summary>
    public const uint MB_ICONINFORMATION = 0x40;
    /// <summary>MessageBox: the warning icon.</summary>
    public const uint MB_ICONEXCLAMATION = 0x30;
    /// <summary>MessageBox: the error icon.</summary>
    public const uint MB_ICONERROR = 0x10;
    /// <summary>SetWindowsHookEx: a keyboard hook on one thread's message queue.</summary>
    public const int WH_KEYBOARD = 2;
    /// <summary>The F1 key.</summary>
    public const int VK_F1 = 0x70;

    /// <summary>MessageBoxW: blocks until the player dismisses it.</summary>
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint owner, string text, string caption, uint type);

    /// <summary>SetWindowsHookExW; 0 on failure.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial nint SetWindowsHookEx(int hookType, nint procedure, nint module, uint threadId);

    /// <summary>UnhookWindowsHookEx.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    /// <summary>CallNextHookEx: passes the event on to the next hook.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    /// <summary>GetAsyncKeyState: the high bit is set while the key is down.</summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    /// <summary>FindWindowA: a top-level window by class and title, either of which may be null; 0 when there is none.</summary>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowA", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FindWindow(string? className, string? windowName);

    /// <summary>SetTimer: the procedure runs on the window's thread when the timer fires; 0 on failure.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint procedure);

    /// <summary>KillTimer.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint window, nuint id);
}
