#pragma once
#include <string>
#include <vector>
#include <windows.h>
#include <intrin.h>
#include <ShlObj_core.h>
#include <filesystem>
#include <iphlpapi.h>
#include <bcrypt.h>

#pragma comment(lib, "IPHLPAPI.lib")
#pragma comment(lib, "Bcrypt.lib")

// Collects platform identity (Steam/Epic) and a hardware fingerprint.
// Hardware fingerprint = SHA256( CPU leaf0 + CPU leaf1 + MoboSerial + MAC )
//   - CPU leaf 0 : vendor ID + max supported leaf
//   - CPU leaf 1 : processor signature (type | family | model | stepping) + feature flags
//   - MoboSerial : SMBIOS Type 2 baseboard serial number (most stable hardware ID)
//   - MAC        : first non-zero physical adapter address (additional entropy)
struct EnvInfo
{
    std::string SteamID;
    std::string GameID;
    std::string AppID;
    std::string EpicID;
    int         CpuLeaf0[4];  // __cpuid(*, 0)
    int         CpuLeaf1[4];  // __cpuid(*, 1)  — processor signature
    std::string CpuIDAsString;
    std::string MoboSerial;   // SMBIOS Type 2 baseboard serial
    std::string HardwareID;   // SHA256 hex string
    bool        IsSteam = false;
    bool        IsEpic  = false;

    EnvInfo()
    {
        SteamID = _Env("SteamID");
        GameID  = _Env("SteamGameId");
        AppID   = _Env("SteamAppId");
        EpicID  = _GetEpicID();

        memset(CpuLeaf0, 0, sizeof(CpuLeaf0));
        memset(CpuLeaf1, 0, sizeof(CpuLeaf1));
        __cpuid(CpuLeaf0, 0);
        __cpuid(CpuLeaf1, 1);

        CpuIDAsString = CpuLeaf0[0] ? std::to_string(CpuLeaf0[0]) : "Unknown";
        MoboSerial    = _GetMotherboardSerial();
        HardwareID    = _ComputeHardwareID();

        _UpdatePlatforms();
        Print();
    }

    // Primary identity sent to the OVS server — Steam preferred, Epic fallback.
    std::string GetIdentity() const
    {
        if (IsSteam) return SteamID;
        if (IsEpic)  return "epic_" + EpicID;
        return "Unknown";
    }

    void Print() const
    {
        printf("[OVS] SteamID    : %s\n", SteamID.c_str());
        printf("[OVS] GameID     : %s\n", GameID.c_str());
        printf("[OVS] AppID      : %s\n", AppID.c_str());
        printf("[OVS] EpicID     : %s\n", EpicID.c_str());
        printf("[OVS] CpuLeaf0   : %d\n", CpuLeaf0[0]);
        printf("[OVS] CpuLeaf1   : 0x%08X  (family/model/stepping)\n", (unsigned int)CpuLeaf1[0]);
        printf("[OVS] MoboSerial : %s\n", MoboSerial.c_str());
        printf("[OVS] HardwareID : %.16s...\n", HardwareID.c_str());
        printf("[OVS] IsSteam    : %s  |  IsEpic : %s\n",
               IsSteam ? "true" : "false", IsEpic ? "true" : "false");
    }

private:
    void _UpdatePlatforms()
    {
        IsSteam = (SteamID != "Unknown" && !SteamID.empty());
        IsEpic  = (EpicID  != "Unknown" && !EpicID.empty());
    }

    static std::string _Env(const char* name)
    {
        const char* v = std::getenv(name);
        return v && v[0] ? std::string(v) : "Unknown";
    }

    // Epic stores the account ID as a 32-char hex filename (no extension base name)
    // inside %LOCALAPPDATA%\..\Local\EpicGamesLauncher\Saved\Data
    std::string _GetEpicID()
    {
        PWSTR path = NULL;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, NULL, &path)))
            return "Unknown";
        std::wstring ws(path);
        CoTaskMemFree(path);

        std::string appData(ws.begin(), ws.end());
        std::string epicPath = appData + "\\..\\Local\\EpicGamesLauncher\\Saved\\Data";

        try {
            for (const auto& entry : std::filesystem::directory_iterator(epicPath)) {
                if (!entry.is_regular_file()) continue;
                std::string name = entry.path().filename().string();
                if (name.find(".dat") == std::string::npos) continue;
                if (name.length() < 32) continue;
                if (name.find('_') != std::string::npos) continue;
                return name.substr(0, 32);
            }
        } catch (...) {}
        return "Unknown";
    }

    // Read SMBIOS Type 2 (Baseboard Information) serial number via GetSystemFirmwareTable.
    // This is the motherboard serial set at the factory — extremely stable.
    // Filters out common OEM placeholder strings.
    std::string _GetMotherboardSerial()
    {
        DWORD size = GetSystemFirmwareTable('RSMB', 0, NULL, 0);
        if (size == 0) return "Unknown";

        std::vector<BYTE> buf(size);
        if (GetSystemFirmwareTable('RSMB', 0, buf.data(), size) != size)
            return "Unknown";

        // Raw SMBIOS blob layout:
        //   BYTE  Used20CallingMethod
        //   BYTE  SMBIOSMajorVersion
        //   BYTE  SMBIOSMinorVersion
        //   BYTE  DmiRevision
        //   DWORD Length          <- byte count of table data that follows
        //   BYTE  SMBIOSTableData[]
        if (size < 8) return "Unknown";

        DWORD tableLen = *reinterpret_cast<DWORD*>(buf.data() + 4);
        BYTE* table    = buf.data() + 8;
        BYTE* tableEnd = table + tableLen;

        BYTE* p = table;
        while (p + 4 <= tableEnd)
        {
            BYTE type = p[0];
            BYTE len  = p[1];

            if (len < 4 || p + len > tableEnd) break;
            if (type == 127) break; // End-of-table marker

            if (type == 2 && len >= 8) // Baseboard Information
            {
                BYTE serialIdx = p[7]; // Serial Number string index (1-based)

                // String table immediately follows the formatted section
                const char* strTable = reinterpret_cast<const char*>(p + len);

                if (serialIdx > 0)
                {
                    const char* s = strTable;
                    // Walk to the (serialIdx)th string
                    for (BYTE i = 1; i < serialIdx; i++)
                    {
                        if (s >= reinterpret_cast<const char*>(tableEnd)) break;
                        s += strlen(s) + 1;
                    }

                    if (s < reinterpret_cast<const char*>(tableEnd) && *s)
                    {
                        std::string serial(s);
                        // Filter out empty strings and OEM placeholder strings
                        if (!serial.empty()                               &&
                            serial != "To Be Filled By O.E.M."           &&
                            serial != "Default string"                    &&
                            serial != "None"                              &&
                            serial != "N/A"                               &&
                            serial != "Not Applicable"                    &&
                            serial.find("00000000") == std::string::npos)
                        {
                            return serial;
                        }
                    }
                }
            }

            // Advance to next structure: skip formatted section, then walk
            // the string table which is terminated by a double null byte.
            const char* strSection = reinterpret_cast<const char*>(p + len);
            while (strSection + 1 < reinterpret_cast<const char*>(tableEnd) &&
                   !(strSection[0] == '\0' && strSection[1] == '\0'))
            {
                strSection++;
            }
            p = reinterpret_cast<BYTE*>(const_cast<char*>(strSection)) + 2;
        }

        return "Unknown";
    }

    // First non-loopback, non-zero physical adapter MAC
    std::string _GetMACAddress()
    {
        IP_ADAPTER_INFO adapters[16];
        DWORD len = sizeof(adapters);
        if (GetAdaptersInfo(adapters, &len) != ERROR_SUCCESS)
            return "000000000000";

        for (IP_ADAPTER_INFO* a = adapters; a; a = a->Next) {
            if (a->AddressLength != 6) continue;
            bool allZero = true;
            for (int i = 0; i < 6; i++) if (a->Address[i]) { allZero = false; break; }
            if (allZero) continue;
            char mac[13] = {};
            sprintf_s(mac, "%02X%02X%02X%02X%02X%02X",
                a->Address[0], a->Address[1], a->Address[2],
                a->Address[3], a->Address[4], a->Address[5]);
            return std::string(mac);
        }
        return "000000000000";
    }

    std::string _SHA256(const std::string& data)
    {
        BCRYPT_ALG_HANDLE  hAlg  = nullptr;
        BCRYPT_HASH_HANDLE hHash = nullptr;
        BYTE hash[32] = {};

        if (BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0)
            return "unknown";
        BCryptCreateHash(hAlg, &hHash, nullptr, 0, nullptr, 0, 0);
        BCryptHashData(hHash, (PUCHAR)data.c_str(), (ULONG)data.size(), 0);
        BCryptFinishHash(hHash, hash, 32, 0);
        BCryptDestroyHash(hHash);
        BCryptCloseAlgorithmProvider(hAlg, 0);

        char hex[65] = {};
        for (int i = 0; i < 32; i++)
            sprintf_s(hex + i * 2, 3, "%02x", hash[i]);
        return std::string(hex);
    }

    // Combines CPU leaf 0 (all 4 regs), CPU leaf 1 (all 4 regs),
    // motherboard serial (SMBIOS Type 2), and MAC address.
    // Mobo serial is the primary stable identifier; MAC adds entropy.
    std::string _ComputeHardwareID()
    {
        std::string mac = _GetMACAddress();

        char buf[512] = {};
        sprintf_s(buf,
            "%d|%d|%d|%d|%08X|%08X|%08X|%08X|%s|%s",
            CpuLeaf0[0], CpuLeaf0[1], CpuLeaf0[2], CpuLeaf0[3],
            (unsigned int)CpuLeaf1[0], (unsigned int)CpuLeaf1[1],
            (unsigned int)CpuLeaf1[2], (unsigned int)CpuLeaf1[3],
            MoboSerial.c_str(),
            mac.c_str()
        );

        return _SHA256(std::string(buf));
    }
};
