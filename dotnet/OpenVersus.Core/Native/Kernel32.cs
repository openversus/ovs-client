using System.Runtime.InteropServices;

namespace OpenVersus.Native;

[StructLayout(LayoutKind.Sequential)]
public struct MEMORY_BASIC_INFORMATION
{
    public nint BaseAddress;
    public nint AllocationBase;
    public uint AllocationProtect;
    public ushort PartitionId;
    public nuint RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_INFO
{
    public uint OemId;
    public uint PageSize;
    public nint MinimumApplicationAddress;
    public nint MaximumApplicationAddress;
    public nuint ActiveProcessorMask;
    public uint NumberOfProcessors;
    public uint ProcessorType;
    public uint AllocationGranularity;
    public ushort ProcessorLevel;
    public ushort ProcessorRevision;
}

public static unsafe partial class Kernel32
{
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint MEM_FREE = 0x10000;
    public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
    public const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleEx(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    private static partial uint GetModuleFileName(nint module, char* buffer, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial nint GetProcAddress(nint module, string name);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint LoadLibrary(string path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION info, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushInstructionCache(nint process, nint address, nuint size);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial void GetSystemInfo(out SYSTEM_INFO info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint bytesRead);

    /// <summary>Full path of a loaded module; 0 means the host executable.</summary>
    public static string GetModulePath(nint module)
    {
        var buffer = new char[32768];
        fixed (char* p = buffer)
        {
            uint length = GetModuleFileName(module, p, (uint)buffer.Length);
            if (length == 0)
            {
                throw new InvalidOperationException($"GetModuleFileNameW failed: {Marshal.GetLastPInvokeError()}");
            }

            return new string(p, 0, (int)length);
        }
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
