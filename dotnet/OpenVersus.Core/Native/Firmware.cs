using System.Runtime.InteropServices;

namespace OpenVersus.Native;

public static partial class Firmware
{
    /// <summary>'RSMB': the raw SMBIOS table.</summary>
    public const uint RSMB = 0x52534D42;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetSystemFirmwareTable(uint provider, uint table, Span<byte> buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);
}
