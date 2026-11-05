using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using OpenVersus.Net;

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
    /// <summary>The device identifiers this machine has, each hashed on its own (<see cref="DeviceIdentity"/>); absent ones are null.</summary>
    public DeviceIds DeviceIds { get; private set; } = new(null, null, null, null, null);
    /// <summary>Whether the firmware names a hypervisor as the system's maker: told to the server, never acted on here.</summary>
    public bool VirtualMachine { get; }
    /// <summary>Whether a Steam id is known.</summary>
    public bool IsSteam { get; set; }
    /// <summary>
    /// The Steam session ticket (ISteamUser::GetAuthSessionTicket) as hex, or "": what proves the Steam id to the server,
    /// which otherwise takes it as a claim and identifies this client by its install id and IP instead. Never printed.
    /// </summary>
    public string SteamTicket { get; set; } = "";

    /// <summary>The Epic account ID token from the game's own EOS SDK (<see cref="EpicIdentity"/>), or "": the server believes an Epic id only with it.</summary>
    public string EpicToken { get; set; } = "";
    /// <summary>Whether an Epic id is known.</summary>
    public bool IsEpic { get; }

    /// <summary>
    /// Collects everything from the environment, cpuid, the firmware table, the registry and the
    /// Epic launcher's files. Nothing here fails: what cannot be read is "Unknown", zero or "".
    /// Off native Windows there is no fingerprint (CrossOver rejects it, Wine's SMBIOS values are
    /// generic, and the Proton one is switched off in the C++ too), and the SteamID variable is
    /// ignored because it can outlive an account switch: only the Steam API is trusted there.
    /// </summary>
    /// <param name="runtime">What the game runs on.</param>
    /// <param name="sources">Where the device identifiers are read from; this machine's registry, firmware and files by default.</param>
    public EnvInfo(RuntimeEnvironment runtime = RuntimeEnvironment.NativeWindows, DeviceSources? sources = null)
    {
        Runtime = runtime;
        _sources = sources ?? DeviceSources.Real();
        SteamId = runtime == RuntimeEnvironment.NativeWindows ? Env("SteamID") : "Unknown";
        GameId = Env("SteamGameId");
        AppId = Env("SteamAppId");
        EpicId = FindEpicId();
        SmbiosInfo? smbios = Smbios.TryRead(_sources.ReadFirmware);
        MotherboardSerial = BaseboardSerial(smbios);
        string machineGuid = _sources.ReadLocalMachine(DeviceIdentity.MachineGuidKey, DeviceIdentity.MachineGuidValue);
        if (runtime == RuntimeEnvironment.NativeWindows)
        {
            if (X86Base.IsSupported)
            {
                CpuLeaf0 = X86Base.CpuId(0, 0);
                CpuLeaf1 = X86Base.CpuId(1, 0);
            }
            // The V2 fingerprint as the server in production knows it: both values, exactly as read, or nothing.
            if (IsStrongSerial(MotherboardSerial) && IsStrongMachineGuid(machineGuid))
            {
                HardwareId = ComputeHardwareIdV2(MotherboardSerial, machineGuid);
                HardwareIdVersion = "2";
                HardwareIdQuality = "strong";
            }
        }
        DeviceIds = DeviceIdentity.Collect(runtime, _sources, smbios, machineGuid, MotherboardSerial);
        VirtualMachine = DeviceIdentity.IsVirtualMachine(smbios);
        IsSteam = SteamId != "Unknown" && SteamId.Length > 0;
        IsEpic = EpicId != "Unknown" && EpicId.Length > 0;
    }

    private readonly DeviceSources _sources;

    /// <summary>
    /// Reads the engine's machine id again when the first read found none: the engine writes it at its own start, which
    /// can come after this client's first registration on a fresh Windows user or Wine prefix. Called before each
    /// registration, so a later one carries it.
    /// </summary>
    public void RefreshDeviceIds()
    {
        if (DeviceIds.UeMachineId == null && DeviceIdentity.ReadUeMachineId(_sources) is { } id)
        {
            DeviceIds = DeviceIds with { UeMachineId = id };
        }
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
        $"[OVS] DeviceIDs  : {DeviceIds.Summary()}\n" +
        $"[OVS] VM         : {(VirtualMachine ? "yes (per the firmware)" : "no")}\n" +
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

    /// <summary>The first SMBIOS type 2 (baseboard) serial that is not one of the usual OEM placeholders, else "Unknown".</summary>
    private static string BaseboardSerial(SmbiosInfo? smbios)
    {
        foreach (string serial in smbios?.BoardSerials ?? [])
        {
            if (serial.Length > 0 && serial != "To Be Filled By O.E.M." && serial != "Default string" && serial != "None"
                && serial != "N/A" && serial != "Not Applicable" && !serial.Contains("00000000"))
            {
                return serial;
            }
        }

        return "Unknown";
    }
}
