using System.Runtime.InteropServices;

namespace OpenVersus.Native;

public static unsafe partial class ConsoleApi
{
    public const int STD_OUTPUT_HANDLE = -11;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4;
    public const uint CP_UTF8 = 65001;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    public static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleMode(nint handle, uint mode);

    [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleTitleW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleTitle(string title);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleOutputCP(uint codePage);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleCP(uint codePage);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteConsole(nint handle, char* text, uint length, out uint written, nint reserved);
}
