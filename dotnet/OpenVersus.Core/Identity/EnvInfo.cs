using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using OpenVersus.Native;

namespace OpenVersus.Identity;

/// <summary>
/// Platform identity (Steam or Epic), this install's random id, and a hardware fingerprint, sent
/// to the server so an account survives an IP change or a switch between Proton and native.
/// The fingerprint is V2 as the C++ EnvInfo built it: native Windows only, SHA-256 of
/// "openversus-device-v2|windows|baseboard serial|machine GUID", and only when both look real.
/// The server keeps nothing else (no V1 fingerprints) and never treats it as the account key:
/// VMs, Wine and OEM placeholder values can make unrelated machines produce the same input.
/// </summary>
public sealed class EnvInfo
{
    /// <summary>What the game runs on.</summary>
    public RuntimeEnvironment Runtime { get; }
    /// <summary>The SteamID environment variable (native Windows only), or "Unknown". <see cref="IdentityRegistration.Run"/> fills it in from <see cref="Identity.SteamId.Resolve"/> when it is unknown.</summary>
    public string SteamId { get; set; } = "Unknown";
    /// <summary>The SteamGameId environment variable, or "Unknown".</summary>
    public string GameId { get; init; } = "Unknown";
    /// <summary>The SteamAppId environment variable, or "Unknown".</summary>
    public string AppId { get; init; } = "Unknown";
    /// <summary>Whether Steam launched the game, which sets SteamGameId and SteamAppId; the game's Steam API then has the player's id.</summary>
    public bool IsSteamLaunch => GameId is not ("Unknown" or "") || AppId is not ("Unknown" or "");
    /// <summary>The Epic account id, from the name of a file in the Epic launcher's saved data, or "Unknown".</summary>
    public string EpicId { get; } = "Unknown";
    /// <summary>This install's random id from <see cref="Config.State.LoadOrCreateInstallId"/>, or "" when none could be stored.</summary>
    public string InstallId { get; set; } = "";
    /// <summary>The UDP port of this machine's rollback node, or 0 when it has none running: where the server sends this player's game for a match between players.</summary>
    public int NodePort { get; set; }
    /// <summary>cpuid leaf 0; all zero off native Windows or where cpuid is not available.</summary>
    public (int Eax, int Ebx, int Ecx, int Edx) CpuLeaf0 { get; }
    /// <summary>cpuid leaf 1; all zero off native Windows or where cpuid is not available.</summary>
    public (int Eax, int Ebx, int Ecx, int Edx) CpuLeaf1 { get; }
    /// <summary>The baseboard serial from the SMBIOS table, or "Unknown". Never logged.</summary>
    public string MotherboardSerial { get; } = "Unknown";
    /// <summary>The V2 fingerprint as lowercase hex, or "" when this machine has none.</summary>
    public string HardwareId { get; } = "";
    /// <summary>"2" with a fingerprint, else "".</summary>
    public string HardwareIdVersion { get; } = "";
    /// <summary>"strong" with a fingerprint, else "".</summary>
    public string HardwareIdQuality { get; } = "";
    /// <summary>Whether a Steam id is known.</summary>
    public bool IsSteam { get; set; }
    /// <summary>
    /// The Steam session ticket (ISteamUser::GetAuthSessionTicket) as hex, or "": what proves the Steam id to the server,
    /// which otherwise takes it as a claim and identifies this client by its install id and IP instead. Never printed.
    /// </summary>
    public string SteamTicket { get; set; } = "";
    /// <summary>Whether an Epic id is known.</summary>
    public bool IsEpic { get; }

    /// <summary>
    /// Collects everything from the environment, cpuid, the firmware table, the registry and the
    /// Epic launcher's files. Nothing here fails: what cannot be read is "Unknown", zero or "".
    /// Off native Windows there is no fingerprint (CrossOver rejects it, Wine's SMBIOS values are
    /// generic, and the Proton one is switched off in the C++ too), and the SteamID variable is
    /// ignored because it can outlive an account switch: only the Steam API is trusted there.
    /// </summary>
    public EnvInfo(RuntimeEnvironment runtime = RuntimeEnvironment.NativeWindows)
    {
        Runtime = runtime;
        SteamId = runtime == RuntimeEnvironment.NativeWindows ? Env("SteamID") : "Unknown";
        GameId = Env("SteamGameId");
        AppId = Env("SteamAppId");
        EpicId = FindEpicId();
        if (runtime == RuntimeEnvironment.NativeWindows)
        {
            if (X86Base.IsSupported)
            {
                CpuLeaf0 = X86Base.CpuId(0, 0);
                CpuLeaf1 = X86Base.CpuId(1, 0);
            }
            MotherboardSerial = ReadBaseboardSerial();
            string machineGuid = Advapi32.ReadLocalMachineString(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
            if (IsStrongSerial(MotherboardSerial) && IsStrongMachineGuid(machineGuid))
            {
                HardwareId = ComputeHardwareIdV2(MotherboardSerial, machineGuid);
                HardwareIdVersion = "2";
                HardwareIdQuality = "strong";
            }
        }
        IsSteam = SteamId != "Unknown" && SteamId.Length > 0;
        IsEpic = EpicId != "Unknown" && EpicId.Length > 0;
    }

    /// <summary>SHA-256 of "openversus-device-v2|windows|serial|machine GUID" as lowercase hex, both exactly as read.</summary>
    public static string ComputeHardwareIdV2(string serial, string machineGuid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"openversus-device-v2|windows|{serial}|{machineGuid}")));

    /// <summary>
    /// Whether a baseboard serial is worth fingerprinting: at least 6 characters (and 6 letters or
    /// digits), no OEM placeholder in it, and not one character repeated.
    /// </summary>
    public static bool IsStrongSerial(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length < 6 || s_placeholders.Any(normalized.Contains))
        {
            return false;
        }

        string compact = string.Concat(normalized.Where(char.IsLetterOrDigit));
        return compact.Length >= 6 && compact.Any(c => c != compact[0]);
    }

    /// <summary>Whether a machine GUID is worth fingerprinting: at least 16 characters, not all zeros or all f, not one character repeated.</summary>
    public static bool IsStrongMachineGuid(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length >= 16
            && normalized != "00000000-0000-0000-0000-000000000000"
            && normalized != "ffffffff-ffff-ffff-ffff-ffffffffffff"
            && normalized.Any(c => c != normalized[0]);
    }

    private static readonly string[] s_placeholders =
    [
        "unknown", "none", "n/a", "default string", "system serial number",
        "to be filled by o.e.m.", "not applicable", "not specified", "00000000", "ffffffff",
    ];

    /// <summary>Every field on its own line for the debug log. The serial, install id and fingerprint are only said to be there or not.</summary>
    public string Print() =>
        $"[OVS] Runtime    : {Identity.Runtime.Name(Runtime)}\n" +
        $"[OVS] SteamID    : {SteamId}\n[OVS] GameID     : {GameId}\n[OVS] AppID      : {AppId}\n[OVS] EpicID     : {EpicId}\n" +
        $"[OVS] InstallID  : {(InstallId.Length > 0 ? "present" : "missing")}\n" +
        $"[OVS] CpuLeaf0   : {CpuLeaf0.Eax}\n[OVS] CpuLeaf1   : 0x{(uint)CpuLeaf1.Eax:X8}  (family/model/stepping)\n" +
        $"[OVS] MoboSerial : {(IsStrongSerial(MotherboardSerial) ? "validated" : "unavailable")}\n" +
        $"[OVS] HardwareID : {(HardwareId.Length > 0 ? "present" : "unavailable")}\n" +
        $"[OVS] HW Version : {(HardwareIdVersion.Length > 0 ? HardwareIdVersion : "none")}  |  Quality : {(HardwareIdQuality.Length > 0 ? HardwareIdQuality : "unavailable")}\n" +
        $"[OVS] IsSteam    : {(IsSteam ? "true" : "false")}  |  IsEpic : {(IsEpic ? "true" : "false")}";

    private static string Env(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? "Unknown" : v;
    }

    /// <summary>
    /// Epic keeps the account id as a 32-character hex file name under the launcher's Saved\Data.
    /// The C++ took the last qualifying .dat it saw, so this does too.
    /// </summary>
    private static string FindEpicId()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                return "Unknown";
            }

            string dir = Path.Combine(local, "..", "Local", "EpicGamesLauncher", "Saved", "Data");
            if (!Directory.Exists(dir))
            {
                return "Unknown";
            }

            string id = "Unknown";
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(file);
                if (name.Contains(".dat") && name.Length >= 32 && !name.Contains('_'))
                {
                    id = name[..32];
                }
            }
            return id;
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>SMBIOS type 2 (baseboard) serial, minus the usual OEM placeholders.</summary>
    private static string ReadBaseboardSerial()
    {
        try
        {
            uint size = Firmware.GetSystemFirmwareTable(Firmware.RSMB, 0, [], 0);
            if (size < 8)
            {
                return "Unknown";
            }

            var buffer = new byte[size];
            if (Firmware.GetSystemFirmwareTable(Firmware.RSMB, 0, buffer, size) != size)
            {
                return "Unknown";
            }

            int tableLength = BitConverter.ToInt32(buffer, 4);
            int table = 8, end = Math.Min(buffer.Length, 8 + tableLength);
            int pos = table;
            while (pos + 4 <= end)
            {
                byte type = buffer[pos], length = buffer[pos + 1];
                if (length < 4 || pos + length > end || type == 127)
                {
                    break;
                }

                if (type == 2 && length >= 8)
                {
                    byte serialIndex = buffer[pos + 7];
                    int s = pos + length;
                    for (int i = 1; i < serialIndex && s < end; i++)
                    {
                        s += StrLen(buffer, s, end) + 1;
                    }

                    if (serialIndex > 0 && s < end && buffer[s] != 0)
                    {
                        string serial = Encoding.UTF8.GetString(buffer, s, StrLen(buffer, s, end));
                        if (serial.Length > 0 && serial != "To Be Filled By O.E.M." && serial != "Default string" && serial != "None"
                            && serial != "N/A" && serial != "Not Applicable" && !serial.Contains("00000000"))
                        {
                            return serial;
                        }
                    }
                }
                int q = pos + length;
                while (q + 1 < end && !(buffer[q] == 0 && buffer[q + 1] == 0))
                {
                    q++;
                }

                pos = q + 2;
            }
        }
        catch { }
        return "Unknown";
    }

    /// <summary>Length of the NUL-terminated string at <paramref name="at"/>, or to <paramref name="end"/> if it never ends.</summary>
    private static int StrLen(byte[] buffer, int at, int end)
    {
        int nul = buffer.AsSpan(at, end - at).IndexOf((byte)0);
        return nul < 0 ? end - at : nul;
    }
}
