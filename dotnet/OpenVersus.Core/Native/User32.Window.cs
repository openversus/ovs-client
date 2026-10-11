using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>The window functions the update progress window uses. Names follow the Windows SDK.</summary>
public static unsafe partial class User32
{
    /// <summary>WS_OVERLAPPED | WS_CAPTION: a title bar and no close button.</summary>
    public const uint WS_CAPTION_ONLY = 0x00C00000;
    /// <summary>WS_CHILD.</summary>
    public const uint WS_CHILD = 0x40000000;
    /// <summary>WS_VISIBLE.</summary>
    public const uint WS_VISIBLE = 0x10000000;
    /// <summary>WS_EX_TOPMOST.</summary>
    public const uint WS_EX_TOPMOST = 0x00000008;
    /// <summary>SW_SHOW.</summary>
    public const int SW_SHOW = 5;
    /// <summary>SM_CXSCREEN.</summary>
    public const int SM_CXSCREEN = 0;
    /// <summary>SM_CYSCREEN.</summary>
    public const int SM_CYSCREEN = 1;
    /// <summary>WM_SETFONT.</summary>
    public const uint WM_SETFONT = 0x0030;
    /// <summary>WM_QUIT.</summary>
    public const uint WM_QUIT = 0x0012;
    /// <summary>PBM_SETRANGE32: a progress bar's range.</summary>
    public const uint PBM_SETRANGE32 = 0x0406;
    /// <summary>PBM_SETPOS: a progress bar's position.</summary>
    public const uint PBM_SETPOS = 0x0402;
    /// <summary>IDC_ARROW.</summary>
    public const int IDC_ARROW = 32512;

    /// <summary>WNDCLASSW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WndClass
    {
        /// <summary>style.</summary>
        public uint Style;
        /// <summary>lpfnWndProc.</summary>
        public nint WndProc;
        /// <summary>cbClsExtra.</summary>
        public int ClassExtra;
        /// <summary>cbWndExtra.</summary>
        public int WindowExtra;
        /// <summary>hInstance.</summary>
        public nint Instance;
        /// <summary>hIcon.</summary>
        public nint Icon;
        /// <summary>hCursor.</summary>
        public nint Cursor;
        /// <summary>hbrBackground.</summary>
        public nint Background;
        /// <summary>lpszMenuName.</summary>
        public char* MenuName;
        /// <summary>lpszClassName.</summary>
        public char* ClassName;
    }

    /// <summary>MSG.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        /// <summary>hwnd.</summary>
        public nint Window;
        /// <summary>message.</summary>
        public uint Message;
        /// <summary>wParam.</summary>
        public nuint WParam;
        /// <summary>lParam.</summary>
        public nint LParam;
        /// <summary>time.</summary>
        public uint Time;
        /// <summary>pt.x.</summary>
        public int X;
        /// <summary>pt.y.</summary>
        public int Y;
        /// <summary>lPrivate.</summary>
        public uint Private;
    }

    /// <summary>RegisterClassW: the class atom, or 0 on failure.</summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    public static partial ushort RegisterClass(in WndClass windowClass);

    /// <summary>CreateWindowExW: the window, or 0 on failure.</summary>
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>ShowWindow.</summary>
    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    /// <summary>DestroyWindow.</summary>
    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    /// <summary>SetWindowTextW; from another thread it waits for the window's thread to take it.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(nint window, string text);

    /// <summary>SendMessageW.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    /// <summary>GetMessageW: 0 on WM_QUIT, -1 on error.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Msg message, nint window, uint filterMin, uint filterMax);

    /// <summary>TranslateMessage.</summary>
    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg message);

    /// <summary>DispatchMessageW.</summary>
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Msg message);

    /// <summary>PostThreadMessageW.</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    /// <summary>GetSystemMetrics.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int index);

    /// <summary>LoadCursorW with a system cursor id.</summary>
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint cursorId);
}

/// <summary>The common controls and stock font the update progress window uses.</summary>
public static partial class Comctl32
{
    /// <summary>ICC_PROGRESS_CLASS.</summary>
    public const uint ICC_PROGRESS_CLASS = 0x20;
    /// <summary>The progress bar's window class.</summary>
    public const string PROGRESS_CLASS = "msctls_progress32";
    /// <summary>DEFAULT_GUI_FONT.</summary>
    public const int DEFAULT_GUI_FONT = 17;

    /// <summary>INITCOMMONCONTROLSEX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InitCommonControls
    {
        /// <summary>dwSize.</summary>
        public uint Size;
        /// <summary>dwICC.</summary>
        public uint Classes;
    }

    /// <summary>InitCommonControlsEx.</summary>
    [LibraryImport("comctl32.dll", EntryPoint = "InitCommonControlsEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(in InitCommonControls init);

    /// <summary>GetStockObject (gdi32).</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    public static partial nint GetStockObject(int index);
}
