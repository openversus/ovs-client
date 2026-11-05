using System.Security.Cryptography;
using System.Text;
using OpenVersus.Native;
using OpenVersus.Net;

namespace OpenVersus.Identity;

/// <summary>
/// Where the device identifiers are read from: the registry, the firmware table and a text file. The real ones on
/// Windows (and Wine, which has all three); nothing anywhere else, so the identity code runs in tests on Linux.
/// </summary>
public sealed class DeviceSources
{
    /// <summary>A REG_SZ value under HKEY_LOCAL_MACHINE, or "".</summary>
    public Func<string, string, string> ReadLocalMachine { get; init; } = (_, _) => "";

    /// <summary>A REG_SZ value under HKEY_CURRENT_USER, or "".</summary>
    public Func<string, string, string> ReadCurrentUser { get; init; } = (_, _) => "";

    /// <summary>The raw SMBIOS table, or null.</summary>
    public Func<byte[]?> ReadFirmware { get; init; } = () => null;

    /// <summary>A small text file's contents, or null when it cannot be read.</summary>
    public Func<string, string?> ReadTextFile { get; init; } = _ => null;

    /// <summary>The process's own machine on Windows; empty sources elsewhere.</summary>
    public static DeviceSources Real() => OperatingSystem.IsWindows()
        ? new DeviceSources
        {
            ReadLocalMachine = Advapi32.ReadLocalMachineString,
            ReadCurrentUser = Advapi32.ReadCurrentUserString,
            ReadFirmware = Firmware.ReadSmbios,
            ReadTextFile = ReadSmallFile,
        }
        : new DeviceSources();

    /// <summary>The first 4 KB of a text file, or null when it is not there or cannot be read.</summary>
    internal static string? ReadSmallFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[4096];
            int read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// The device identifiers sent with the registration, each hashed on its own, so the server can ban a person through
/// any one of them and an operator can see which one changed between two logins. None of them picks an account: that
/// stays with the Steam and Epic ids, the install id and the V2 fingerprint. Each is sent only when its value is worth
/// something (a real id, not an OEM placeholder); the rest are absent.
/// </summary>
public static class DeviceIdentity
{
    /// <summary>The hash's domain: "openversus-device-v3|kind|value", the value trimmed and lowercased.</summary>
    public const string HashPrefix = "openversus-device-v3";

    public const string MachineGuidKind = "machineGuid";
    public const string BoardSerialKind = "boardSerial";
    public const string SystemUuidKind = "systemUuid";
    public const string UeMachineIdKind = "ueMachineId";
    public const string OsMachineIdKind = "osMachineId";

    /// <summary>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid: one per Windows install, and one per Wine prefix.</summary>
    public const string MachineGuidKey = @"SOFTWARE\Microsoft\Cryptography";
    public const string MachineGuidValue = "MachineGuid";

    /// <summary>
    /// HKCU\Software\Epic Games\Unreal Engine\Identifiers\MachineId: the GUID the engine's FPlatformMisc::GetMachineId
    /// mints at its first run and writes to the registry (the MachineId and LoginId of every crash report). One per
    /// Windows user, shared by every Unreal game they run; one per Wine prefix. Only read here, never created.
    /// </summary>
    public const string UeMachineIdKey = @"Software\Epic Games\Unreal Engine\Identifiers";
    public const string UeMachineIdValue = "MachineId";

    /// <summary>The Linux machine id (systemd's /etc/machine-id, one per OS install), through Wine's Z: drive.</summary>
    public const string OsMachineIdPath = @"Z:\etc\machine-id";

    /// <summary>Lowercase hex SHA-256 of <see cref="HashPrefix"/>|<paramref name="kind"/>|<paramref name="value"/>, the value trimmed and lowercased.</summary>
    public static string Hash(string kind, string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{HashPrefix}|{kind}|{value.Trim().ToLowerInvariant()}")));

    /// <summary>
    /// Every identifier this machine has: the machine GUID (Windows, or the prefix's), the baseboard serial and system
    /// UUID as the firmware table carries them, the engine's machine id, and off Windows the Linux machine id. Under
    /// Wine the firmware table is Wine's own: it cannot read the real serial and UUID (root only in sysfs) and fills
    /// both with the Linux machine id (seen with Wine 11.20), which is unique per OS install all the same, so it is
    /// taken as any other unique value is. Only the Linux id is never looked for on Windows, where Z: is a real drive.
    /// </summary>
    public static DeviceIds Collect(RuntimeEnvironment runtime, DeviceSources sources, SmbiosInfo? smbios, string machineGuid, string boardSerial) => new(
        HashIfStrong(MachineGuidKind, machineGuid, EnvInfo.IsStrongMachineGuid),
        HashIfStrong(BoardSerialKind, boardSerial, EnvInfo.IsStrongSerial),
        HashIfStrong(SystemUuidKind, smbios?.SystemUuid ?? "", IsStrongUuid),
        ReadUeMachineId(sources),
        runtime == RuntimeEnvironment.NativeWindows ? null : HashIfStrong(OsMachineIdKind, sources.ReadTextFile(OsMachineIdPath) ?? "", IsStrongHex32));

    /// <summary>The engine's machine id, hashed, or null while the registry has none.</summary>
    public static string? ReadUeMachineId(DeviceSources sources) => HashIfStrong(UeMachineIdKind, sources.ReadCurrentUser(UeMachineIdKey, UeMachineIdValue), IsStrongHex32);

    /// <summary>
    /// Whether the firmware says this is a virtual machine (the system manufacturer or product names a hypervisor).
    /// Information for the operator: a VM's identifiers are its own, not its host's.
    /// </summary>
    public static bool IsVirtualMachine(SmbiosInfo? smbios)
    {
        if (smbios == null)
        {
            return false;
        }

        string text = $"{smbios.Manufacturer} {smbios.Product}".ToLowerInvariant();
        return s_hypervisors.Any(text.Contains);
    }

    private static readonly string[] s_hypervisors =
    [
        "vmware", "virtualbox", "innotek", "qemu", "kvm", "bochs", "xen", "parallels", "virtual machine", "bhyve", "hyper-v",
    ];

    /// <summary>A system UUID worth sending: 8-4-4-4-12 hex, not all one digit, and not the OEM dummy sequence.</summary>
    public static bool IsStrongUuid(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (!Guid.TryParseExact(normalized, "D", out _) || s_dummyUuids.Contains(normalized))
        {
            return false;
        }

        string digits = normalized.Replace("-", "");
        return digits.Any(c => c != digits[0]);
    }

    // 00 02 00 03 00 04 00 05 00 06 00 07 00 08 00 09, in both byte orders.
    private static readonly string[] s_dummyUuids = ["03000200-0400-0500-0006-000700080009", "00020003-0004-0005-0006-000700080009"];

    /// <summary>A 32-hex-digit id (the engine's machine id, Linux's machine id) that is not one digit repeated.</summary>
    public static bool IsStrongHex32(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 32 && normalized.All(char.IsAsciiHexDigit) && normalized.Any(c => c != normalized[0]);
    }

    private static string? HashIfStrong(string kind, string value, Func<string, bool> strong) => strong(value) ? Hash(kind, value) : null;
}
