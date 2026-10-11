using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>STARTUPINFOW, as CreateProcessW reads it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct STARTUPINFOW
{
    public uint cb;
    public nint lpReserved;
    public nint lpDesktop;
    public nint lpTitle;
    public uint dwX;
    public uint dwY;
    public uint dwXSize;
    public uint dwYSize;
    public uint dwXCountChars;
    public uint dwYCountChars;
    public uint dwFillAttribute;
    public uint dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public nint lpReserved2;
    public nint hStdInput;
    public nint hStdOutput;
    public nint hStdError;
}

/// <summary>PROCESS_INFORMATION: what CreateProcessW hands back.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_INFORMATION
{
    public nint hProcess;
    public nint hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

/// <summary>IO_COUNTERS, part of the extended job limits.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

/// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION, for JobObjectExtendedLimitInformation.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>Process and job APIs, for the rollback node the mod runs beside the game. Names follow the Windows SDK.</summary>
public static unsafe partial class Kernel32
{
    /// <summary>CREATE_NO_WINDOW: a console process without a console window.</summary>
    public const uint CREATE_NO_WINDOW = 0x08000000;
    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.</summary>
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    /// <summary>JobObjectExtendedLimitInformation.</summary>
    public const int JobObjectExtendedLimitInformation = 9;
    /// <summary>STILL_ACTIVE, GetExitCodeProcess's code for a running process.</summary>
    public const uint STILL_ACTIVE = 259;
    /// <summary>INFINITE, for WaitForSingleObject.</summary>
    public const uint INFINITE = 0xFFFFFFFF;
    /// <summary>WAIT_OBJECT_0.</summary>
    public const uint WAIT_OBJECT_0 = 0;

    /// <summary>CreateProcessW. The command line is writable, as the API requires.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcess(string? applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory,
        in STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    /// <summary>CreateJobObjectW, unnamed.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    public static partial nint CreateJobObject(nint attributes, nint name);

    /// <summary>SetInformationJobObject.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(nint job, int informationClass, void* information, uint length);

    /// <summary>AssignProcessToJobObject.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(nint job, nint process);

    /// <summary>GetExitCodeProcess; the code is STILL_ACTIVE while the process runs.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    /// <summary>WaitForSingleObject.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    /// <summary>CloseHandle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// <summary>GetProcessHeap: the heap Wine's path conversion allocates from.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetProcessHeap();

    /// <summary>HeapFree.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HeapFree(nint heap, uint flags, nint memory);
}
