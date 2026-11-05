using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>The SMBIOS read the identity code uses, and the TerminateProcess the updater exits with. Names follow the Windows SDK.</summary>
public static partial class Firmware
{
    /// <summary>'RSMB': the raw SMBIOS table.</summary>
    public const uint RSMB = 0x52534D42;

    /// <summary>GetSystemFirmwareTable: the bytes written, or the size needed when the buffer is too small; 0 on failure.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetSystemFirmwareTable(uint provider, uint table, Span<byte> buffer, uint size);

    /// <summary>The raw SMBIOS table (its 8-byte header included), or null when the system has none or the read fails.</summary>
    public static byte[]? ReadSmbios()
    {
        uint size = GetSystemFirmwareTable(RSMB, 0, [], 0);
        if (size < 8)
        {
            return null;
        }

        var buffer = new byte[size];
        return GetSystemFirmwareTable(RSMB, 0, buffer, size) == size ? buffer : null;
    }

    /// <summary>TerminateProcess.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);
}
