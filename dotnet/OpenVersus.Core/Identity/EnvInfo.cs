using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using OpenVersus.Native;

namespace OpenVersus.Identity;

/// <summary>
/// Platform identity (Steam or Epic) and a hardware fingerprint, sent to the server so an
/// account survives an IP change or a switch between Proton and native. The fingerprint is
/// SHA-256 of "cpuid leaf 0 (four registers, signed decimal) | leaf 1 eax (hex) | baseboard
/// serial", built with the exact text the C++ EnvInfo built, because the server keys on it.
/// </summary>
public sealed class EnvInfo
{
    public string SteamId { get; set; } = "Unknown";
    public string GameId { get; } = "Unknown";
    public string AppId { get; } = "Unknown";
    public string EpicId { get; } = "Unknown";
    public (int Eax, int Ebx, int Ecx, int Edx) CpuLeaf0 { get; }
    public (int Eax, int Ebx, int Ecx, int Edx) CpuLeaf1 { get; }
    public string MotherboardSerial { get; }
    public string HardwareId { get; }
    public bool IsSteam { get; set; }
    public bool IsEpic { get; }

    public EnvInfo()
    {
        SteamId = Env("SteamID");
        GameId = Env("SteamGameId");
        AppId = Env("SteamAppId");
        EpicId = FindEpicId();
        if (X86Base.IsSupported)
        {
            CpuLeaf0 = X86Base.CpuId(0, 0);
            CpuLeaf1 = X86Base.CpuId(1, 0);
        }
        MotherboardSerial = ReadBaseboardSerial();
        HardwareId = ComputeHardwareId(CpuLeaf0, CpuLeaf1, MotherboardSerial);
        IsSteam = SteamId != "Unknown" && SteamId.Length > 0;
        IsEpic = EpicId != "Unknown" && EpicId.Length > 0;
    }

    /// <summary>The fingerprint text before hashing, exactly as the C++ formatted it ("%d|%d|%d|%d|%08X|%ls").</summary>
    public static string FingerprintText((int Eax, int Ebx, int Ecx, int Edx) leaf0, (int Eax, int Ebx, int Ecx, int Edx) leaf1, string serial) =>
        $"{leaf0.Eax}|{leaf0.Ebx}|{leaf0.Ecx}|{leaf0.Edx}|{(uint)leaf1.Eax:X8}|{serial}";

    public static string ComputeHardwareId((int, int, int, int) leaf0, (int, int, int, int) leaf1, string serial) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintText(leaf0, leaf1, serial))));

    public string Print() =>
        $"[OVS] SteamID    : {SteamId}\n[OVS] GameID     : {GameId}\n[OVS] AppID      : {AppId}\n[OVS] EpicID     : {EpicId}\n" +
        $"[OVS] CpuLeaf0   : {CpuLeaf0.Eax}\n[OVS] CpuLeaf1   : 0x{(uint)CpuLeaf1.Eax:X8}  (family/model/stepping)\n" +
        $"[OVS] MoboSerial : {MotherboardSerial}\n[OVS] HardwareID : {HardwareId[..Math.Min(16, HardwareId.Length)]}...\n" +
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
