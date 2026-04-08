#pragma once
#include "utils/Utils.hpp"
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

using OVS::Utils::StringBuilder;
using OVS::Utils::DebugPrintWrapper;

// Collects platform identity (Steam/Epic) and a hardware fingerprint.
// Hardware fingerprint = SHA256( CPU leaf0 + CPU leaf1 + MoboSerial )
//   - CPU leaf 0 : vendor ID + max supported leaf
//   - CPU leaf 1 : processor signature (type | family | model | stepping) + feature flags
//   - MoboSerial : SMBIOS Type 2 baseboard serial number (most stable hardware ID)
struct EnvInfo
{
    std::wstring SteamID;
    std::wstring GameID;
    std::wstring AppID;
    std::wstring EpicID;
    int         CpuLeaf0[4];
    int         CpuLeaf1[4];  // __cpuid(*, 1)  — processor signature
    std::wstring CpuIDAsString;
    std::wstring MoboSerial;   // SMBIOS Type 2 baseboard serial
    std::wstring HardwareID;   // SHA256 hex string
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

        CpuIDAsString = CpuLeaf0[0] ? std::to_wstring(CpuLeaf0[0]) : L"Unknown";
        MoboSerial    = _GetMotherboardSerial();
        HardwareID    = _ComputeHardwareID();

        _UpdatePlatforms();
    }

    // Primary identity sent to the OVS server — Steam preferred, Epic fallback.
    std::wstring GetIdentity() const
    {
        if (IsSteam) return SteamID;
        if (IsEpic) return L"epic_" + EpicID;
        return L"Unknown";
    }

    std::wstring Print(bool dumpString = false)
    {
        StringBuilder sb;
        sb.Append(L"[OVS] SteamID    : %ls\n", SteamID.c_str())
          .Append(L"[OVS] GameID     : %ls\n", GameID.c_str())
          .Append(L"[OVS] AppID      : %ls\n", AppID.c_str())
          .Append(L"[OVS] EpicID     : %ls\n", EpicID.c_str())
          .Append(L"[OVS] CpuLeaf0   : %d\n", CpuLeaf0[0])
          .Append(L"[OVS] CpuLeaf1   : 0x%08X  (family/model/stepping)\n", (unsigned int)CpuLeaf1[0])
          .Append(L"[OVS] MoboSerial : %ls\n", MoboSerial.c_str())
          .Append(L"[OVS] HardwareID : %.16ls...\n", HardwareID.c_str())
          .Append(L"[OVS] IsSteam    : %ls  |  IsEpic : %ls\n",
              IsSteam ? L"true" : L"false", IsEpic ? L"true" : L"false");

        if (dumpString)
        {
            PrintDebug(sb.ToString());
        }

        return sb.ToString();
    }

private:
    void _UpdatePlatforms()
    {
        IsSteam = (SteamID != L"Unknown" && !SteamID.empty());
        IsEpic  = (EpicID  != L"Unknown" && !EpicID.empty());
    }

    static std::wstring _Env(const char* name)
    {
        const char* v = std::getenv(name);
        if (v && v[0]) {
            int len = MultiByteToWideChar(CP_UTF8, 0, v, -1, nullptr, 0);
            if (len > 0) {
                std::wstring result(len - 1, L'\0');
                MultiByteToWideChar(CP_UTF8, 0, v, -1, &result[0], len);
                return result;
            }
        }
        return L"Unknown";
    }

    // Epic stores the account ID as a 32-char hex filename (no extension base name)
    // inside %LOCALAPPDATA%\...\Local\EpicGamesLauncher\Saved\Data
    std::wstring _GetEpicID()
    {
        std::wstring EpicID = L"Unknown";
        PWSTR path = NULL;
        HRESULT AppDataPathExists = SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, NULL, &path);
        if (SUCCEEDED(AppDataPathExists))
        {
            std::wstring AppDataPath(path);
            std::wstring EpicIDFilePath = AppDataPath + L"\\..\\Local\\EpicGamesLauncher\\Saved\\Data";
            try
            {
                for (const auto& entry : std::filesystem::directory_iterator(EpicIDFilePath))
                {
                    if (entry.is_regular_file())
                    {
                        std::wstring filename = entry.path().filename().wstring();
                        if (filename.find(L".dat") != std::wstring::npos)
                        {
                            if (filename.length() < 32)
                            {
                                continue;
                            }

                            if (filename.find(L"_") != std::wstring::npos)
                            {
                                continue;
                            }
                            EpicID = filename.substr(0, 32);
                        }
                    }
                }
            }
            catch (...) {}
            CoTaskMemFree(path);
        }

        return EpicID;
    }

    // Read SMBIOS Type 2 (Baseboard Information) serial number via GetSystemFirmwareTable.
    // This is the motherboard serial set at the factory — extremely stable.
    // Filters out common OEM placeholder strings.
    std::wstring _GetMotherboardSerial()
    {
        DWORD size = GetSystemFirmwareTable('RSMB', 0, NULL, 0);
        if (size == 0) return L"Unknown";

        std::vector<BYTE> buf(size);
        if (GetSystemFirmwareTable('RSMB', 0, buf.data(), size) != size)
            return L"Unknown";

        // Raw SMBIOS blob layout:
        //   BYTE  Used20CallingMethod
        //   BYTE  SMBIOSMajorVersion
        //   BYTE  SMBIOSMinorVersion
        //   BYTE  DmiRevision
        //   DWORD Length          <- byte count of table data that follows
        //   BYTE  SMBIOSTableData[]
        if (size < 8) return L"Unknown";

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
                            // Convert to wide string
                            int len = MultiByteToWideChar(CP_UTF8, 0, serial.c_str(), -1, nullptr, 0);
                            if (len > 0) {
                                std::wstring result(len - 1, L'\0');
                                MultiByteToWideChar(CP_UTF8, 0, serial.c_str(), -1, &result[0], len);
                                return result;
                            }
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

        return L"Unknown";
    }

    // First non-loopback, non-zero physical adapter MAC
    std::wstring _GetMACAddress()
    {
        IP_ADAPTER_INFO adapters[16];
        DWORD len = sizeof(adapters);
        if (GetAdaptersInfo(adapters, &len) != ERROR_SUCCESS)
            return L"000000000000";

        for (IP_ADAPTER_INFO* a = adapters; a; a = a->Next) {
            if (a->AddressLength != 6) continue;
            bool allZero = true;
            for (int i = 0; i < 6; i++) if (a->Address[i]) { allZero = false; break; }
            if (allZero) continue;
            wchar_t mac[13] = {};
            swprintf_s(mac, 13, L"%02X%02X%02X%02X%02X%02X",
                a->Address[0], a->Address[1], a->Address[2],
                a->Address[3], a->Address[4], a->Address[5]);
            return std::wstring(mac);
        }
        return L"000000000000";
    }

    std::wstring _SHA256(const std::wstring& data)
    {
        BCRYPT_ALG_HANDLE  hAlg  = nullptr;
        BCRYPT_HASH_HANDLE hHash = nullptr;
        BYTE hash[32] = {};

        // Convert wstring to UTF-8 for hashing
        int utf8Len = WideCharToMultiByte(CP_UTF8, 0, data.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (utf8Len <= 0) return L"unknown";
        std::string utf8Data(utf8Len - 1, '\0');
        WideCharToMultiByte(CP_UTF8, 0, data.c_str(), -1, &utf8Data[0], utf8Len, nullptr, nullptr);

        if (BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0)
            return L"unknown";
        BCryptCreateHash(hAlg, &hHash, nullptr, 0, nullptr, 0, 0);
        BCryptHashData(hHash, (PUCHAR)utf8Data.c_str(), (ULONG)utf8Data.size(), 0);
        BCryptFinishHash(hHash, hash, 32, 0);
        BCryptDestroyHash(hHash);
        BCryptCloseAlgorithmProvider(hAlg, 0);

        wchar_t hex[65] = {};
        for (int i = 0; i < 32; i++)
            swprintf_s(hex + i * 2, 3, L"%02x", hash[i]);
        return std::wstring(hex);
    }

    // Combines CPU leaf 0 (all 4 regs), CPU leaf 1 (all 4 regs),
    // and motherboard serial (SMBIOS Type 2).
    // MAC excluded — too volatile due to VPNs, virtual adapters, and NIC changes.
    std::wstring _ComputeHardwareID()
    {
        wchar_t buf[512] = {};
        swprintf_s(buf, 512,
            L"%d|%d|%d|%d|%08X|%ls",
            CpuLeaf0[0], CpuLeaf0[1], CpuLeaf0[2], CpuLeaf0[3],
            (unsigned int)CpuLeaf1[0],  // processor signature only (family/model/stepping)
            MoboSerial.c_str()
        );

        return _SHA256(std::wstring(buf));
    }
};
