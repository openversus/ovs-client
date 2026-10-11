using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>The console functions and constants <see cref="OpenVersus.ConsoleWindow"/> uses. Names follow the Windows SDK.</summary>
public static unsafe partial class ConsoleApi
{
    /// <summary>GetStdHandle: standard output.</summary>
    public const int STD_OUTPUT_HANDLE = -11;
    /// <summary>SetConsoleMode: interpret ANSI escape sequences, for colored output.</summary>
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4;
    /// <summary>The UTF-8 code page.</summary>
    public const uint CP_UTF8 = 65001;

    /// <summary>AllocConsole: gives the process a console; false when it already has one.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllocConsole();

    /// <summary>FreeConsole.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    /// <summary>GetStdHandle.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetStdHandle(int which);

    /// <summary>GetConsoleMode.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetConsoleMode(nint handle, out uint mode);

    /// <summary>SetConsoleMode.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleMode(nint handle, uint mode);

    /// <summary>SetConsoleTitleW.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleTitleW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleTitle(string title);

    /// <summary>SetConsoleOutputCP.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleOutputCP(uint codePage);

    /// <summary>SetConsoleCP.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleCP(uint codePage);

    /// <summary>WriteConsoleW: UTF-16 straight to the console, whatever the code page.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteConsole(nint handle, char* text, uint length, out uint written, nint reserved);
}
