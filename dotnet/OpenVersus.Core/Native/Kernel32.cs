using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>MEMORY_BASIC_INFORMATION: one region, as VirtualQuery describes it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MEMORY_BASIC_INFORMATION
{
    /// <summary>The start of the region.</summary>
    public nint BaseAddress;
    /// <summary>The start of the allocation the region belongs to.</summary>
    public nint AllocationBase;
    /// <summary>The protection the allocation was made with.</summary>
    public uint AllocationProtect;
    /// <summary>The partition; unused here.</summary>
    public ushort PartitionId;
    /// <summary>The region's size in bytes; every page in it has the same attributes.</summary>
    public nuint RegionSize;
    /// <summary>MEM_COMMIT, MEM_RESERVE or MEM_FREE.</summary>
    public uint State;
    /// <summary>The pages' current protection, a PAGE_* value.</summary>
    public uint Protect;
    /// <summary>MEM_IMAGE, MEM_MAPPED or MEM_PRIVATE.</summary>
    public uint Type;
}

/// <summary>SYSTEM_INFO, from GetSystemInfo.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_INFO
{
    /// <summary>The processor architecture (in its low 16 bits) and a reserved word.</summary>
    public uint OemId;
    /// <summary>The page size.</summary>
    public uint PageSize;
    /// <summary>The lowest address a process can use.</summary>
    public nint MinimumApplicationAddress;
    /// <summary>The highest address a process can use.</summary>
    public nint MaximumApplicationAddress;
    /// <summary>The processors configured into the system, one bit each.</summary>
    public nuint ActiveProcessorMask;
    /// <summary>The number of logical processors.</summary>
    public uint NumberOfProcessors;
    /// <summary>Obsolete; kept for the layout.</summary>
    public uint ProcessorType;
    /// <summary>The granularity VirtualAlloc reserves in, 64 KB on Windows; the size of one trampoline page.</summary>
    public uint AllocationGranularity;
    /// <summary>The architecture-dependent processor level.</summary>
    public ushort ProcessorLevel;
    /// <summary>The architecture-dependent processor revision.</summary>
    public ushort ProcessorRevision;
}

/// <summary>The kernel32 functions and constants the client uses. Names follow the Windows SDK.</summary>
public static unsafe partial class Kernel32
{
    /// <summary>Read-only pages.</summary>
    public const uint PAGE_READONLY = 0x02;
    /// <summary>Read-write pages.</summary>
    public const uint PAGE_READWRITE = 0x04;
    /// <summary>Read-execute pages.</summary>
    public const uint PAGE_EXECUTE_READ = 0x20;
    /// <summary>Read-write-execute pages.</summary>
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    /// <summary>VirtualAlloc: commit the pages; VirtualQuery: the region is committed.</summary>
    public const uint MEM_COMMIT = 0x1000;
    /// <summary>VirtualAlloc: reserve the range; VirtualQuery: the region is reserved.</summary>
    public const uint MEM_RESERVE = 0x2000;
    /// <summary>VirtualFree: release the whole allocation.</summary>
    public const uint MEM_RELEASE = 0x8000;
    /// <summary>VirtualQuery: the region is free.</summary>
    public const uint MEM_FREE = 0x10000;
    /// <summary>GetModuleHandleEx: the argument is an address inside the module, not a name.</summary>
    public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
    /// <summary>GetModuleHandleEx: leave the module's reference count alone.</summary>
    public const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;

    /// <summary>GetModuleHandleW: a loaded module by name; null for the host executable. 0 when it is not loaded.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint GetModuleHandle(string? moduleName);

    /// <summary>GetModuleHandleExW.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleEx(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    private static partial uint GetModuleFileName(nint module, char* buffer, uint size);

    /// <summary>GetProcAddress: an export by name; 0 when there is none.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial nint GetProcAddress(nint module, string name);

    /// <summary>LoadLibraryW.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint LoadLibrary(string path);

    /// <summary>VirtualProtect: sets the pages' protection and returns the old one.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualQuery", SetLastError = true)]
    private static partial nuint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION info, nuint length);

    /// <summary>The region containing <paramref name="address"/>; 0 when the query fails.</summary>
    public static nuint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION info) =>
        VirtualQuery(address, out info, (nuint)Unsafe.SizeOf<MEMORY_BASIC_INFORMATION>());

    /// <summary>VirtualAlloc.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    /// <summary>VirtualFree.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(nint address, nuint size, uint freeType);

    /// <summary>FlushInstructionCache, after code has been written.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushInstructionCache(nint process, nint address, nuint size);

    /// <summary>GetCurrentProcess: the pseudo-handle for this process.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    /// <summary>GetCurrentThreadId.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    /// <summary>GetSystemInfo.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial void GetSystemInfo(out SYSTEM_INFO info);

    /// <summary>ReadProcessMemory: a copy that reports an unreadable page instead of faulting.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint process, nint address, Span<byte> buffer, nuint size, out nuint bytesRead);

    /// <summary>Full path of a loaded module; 0 means the host executable.</summary>
    public static string GetModulePath(nint module)
    {
        var buffer = new char[32768];
        uint length;
        fixed (char* p = buffer)
        {
            length = GetModuleFileName(module, p, (uint)buffer.Length);
        }

        if (length == 0)
        {
            throw new InvalidOperationException($"GetModuleFileNameW failed: {Marshal.GetLastPInvokeError()}");
        }

        return new string(buffer, 0, (int)length);
    }

    /// <summary>The module containing <paramref name="address"/>, typically one of our own exports.</summary>
    public static nint ModuleFromAddress(nint address)
    {
        if (!GetModuleHandleEx(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, address, out nint module))
        {
            throw new InvalidOperationException($"GetModuleHandleExW failed: {Marshal.GetLastPInvokeError()}");
        }

        return module;
    }
}
