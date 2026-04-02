#include "includes.h"
#include "ovs/OVSUtils.h"
#include "mvs/mvs.h"
#include "ovs/OpenVersus.h"
#include "ovs/EnvInfo.h"
#include "utils/prettyprint.h"
#include "Utils/Utils.hpp"
#include <tlhelp32.h>
#include <VersionHelpers.h>
#include <string>
#include <vector>
#include <iostream>
#include <filesystem>
#include <fstream>
#include <winhttp.h>
#include <wininet.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "wininet.lib")
#pragma comment(lib, "ws2_32.lib")

namespace fs = std::filesystem;
using namespace OVS::Utils;

static const std::wstring CURRENT_HOOK_VERSION = OVS::GetCurrentVersion();

EnvInfo* GEnvInfo = nullptr;
Trampoline* GameTramp, * User32Tramp;
ConsoleColors debugColor = ConsoleColors::YELLOW;

void CreateConsole();
void SpawnError(const wchar_t* msg);
void PreGameHooks();
void ProcessSettings();
bool OnInitializeHook();
void SpawnP2PServer() {};
void DespawnP2PServer() {};

LRESULT CALLBACK KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
{
    bool state = lParam >> 31, transition = lParam & 0x40000000;
    auto RepeatCount = LOWORD(lParam);

    if (code >= 0 && !state)
    {
        if (GetAsyncKeyState(VK_F1))
        {
            wprintf(L"Attempting F1 action\n");
            //OVS::ShowNotification("OVS Loaded", CURRENT_HOOK_VERSION, 10.f, true);
        }

    }

    return CallNextHookEx(0, code, wParam, lParam);
}

void CreateConsole()
{
    FreeConsole();
    AllocConsole();

    FILE* fNull;
    freopen_s(&fNull, "CONOUT$", "w", stdout);
    freopen_s(&fNull, "CONOUT$", "w", stderr);

    bool isUTF8 = SetLocaleConfig();

    std::wstring consoleName = L"OpenVersus Debug Console";
    SetConsoleTitleW(consoleName.c_str());
    HookMetadata::Console = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD dwMode = 0;
    GetConsoleMode(HookMetadata::Console, &dwMode);
    dwMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
    SetConsoleMode(HookMetadata::Console, dwMode);

    printfCyan(L"OpenVersus - It's better than Parsec\n");
    printfCyan(L"v%ls\n\n", CURRENT_HOOK_VERSION.c_str());
    printfCyan(L"Binary releases available at: https://github.com/christopher-conley/OpenVersus\n");
    printfCyan(L"Source code and binary releases available at: https://github.com/openversus\n");
    printfCyan(L"Maintained by RosettaSt0ned and the MVS community\n");

    printfCyan(L"OpenVersus is originally based on the publicly-available code developed by thethiny and multiversuskoth, located at: \n");
    printfCyan(L"https://github.com/thethiny/MVSIASI\n");
    printfCyan(L"https://github.com/multiversuskoth/mvs-http-server\n");
    printfCyan(L"https://github.com/multiversuskoth/mvs-udp-server\n");
}

void PreGameHooks()
{
    GameTramp = Trampoline::MakeTrampoline(GetModuleHandle(nullptr));
    if (SettingsMgr->iLogLevel)
        wprintf(L"Generated Trampolines\n");
     IATable = ParsePEHeader();


    if (SettingsMgr->bDisableSignatureCheck)
    {
        HookMetadata::sActiveMods.bAntiSigCheck			= OVS::Hooks::DisableSignatureCheck(GameTramp);
    }
    if (SettingsMgr->bEnableServerProxy)
    {
        HookMetadata::sActiveMods.bGameEndpointSwap		= OVS::Hooks::OverrideGameEndpointsData(GameTramp);
    }
    if (SettingsMgr->bEnableProdServerProxy)
    {
        HookMetadata::sActiveMods.bProdEndpointSwap		= OVS::Hooks::OverrideProdEndpointsData(GameTramp);
    }
    if (SettingsMgr->bSunsetDate)
    {
        HookMetadata::sActiveMods.bSunsetDate			= OVS::Hooks::PatchSunsetSetterIntoOVSChecker(GameTramp);
    }
    if (SettingsMgr->bHookUE)
    {
        HookMetadata::sActiveMods.bUEFuncs				= OVS::Hooks::HookUEFuncs(GameTramp);
    }
    if (SettingsMgr->bDialog)
    {
        HookMetadata::sActiveMods.bDialog				= OVS::Hooks::DialogHooks(GameTramp);
    }
    if (SettingsMgr->bNotifs)
    {
        HookMetadata::sActiveMods.bNotifs = OVS::Hooks::NotificationHooks(GameTramp);
    }
}

void ProcessSettings()
{
    // KeyBinds
    SettingsMgr->iVKMenuToggle			= StringToVK(SettingsMgr->hkMenu);

    // DLL Procs
    HookMetadata::sLFS.ModLoader		= ParseLibFunc(SettingsMgr->szModLoader);
    HookMetadata::sLFS.AntiCheatEngine	= ParseLibFunc(SettingsMgr->szAntiCheatEngine);
    HookMetadata::sLFS.CurlSetOpt		= ParseLibFunc(SettingsMgr->szCurlSetOpt);
    HookMetadata::sLFS.CurlPerform		= ParseLibFunc(SettingsMgr->szCurlPerform);

    printfCyan(L"Parsed Settings\n");
}

void SpawnError(const wchar_t* msg)
{
    MessageBoxW(NULL, msg, L"OpenVersus", MB_ICONEXCLAMATION);
}

bool HandleWindowsVersion()
{
    if (IsWindows10OrGreater())
    {
        return true;
    }

    if (IsWindows7SP1OrGreater())
    {
        SpawnError(L"OVS doesn't officially support Windows 8 or 7 SP1. It may misbehave.");
        return true;
    }

    SpawnError(L"OVS doesn't support Windows 7 or Earlier. Might not work.");
    return true;


}

inline bool VerifyProcessName(std::wstring expected_process) {
    std::wstring process_name = GetProcessNameW();

    for (size_t i = 0; i < process_name.length(); ++i) {
        process_name[i] = std::tolower(process_name[i]);
    }

    for (size_t i = 0; i < expected_process.length(); ++i) {
        expected_process[i] = std::tolower(expected_process[i]);
    }

    return (process_name == expected_process);
}

void InitializeKeyboard()
{
    HookMetadata::KeyboardProcHook = SetWindowsHookEx(
        WH_KEYBOARD,
        KeyboardProc,
        HookMetadata::CurrentDllModule,
        GetCurrentThreadId()
    );
}

// Returns true if running under Proton/Wine (winex11.drv is only present in Wine)
static bool IsProton()
{
    return GetModuleHandleA("winex11.drv") != nullptr;
}

// WinHTTP POST — works on Proton/Steam Deck (Wine's WinHTTP is solid)
static bool PostViaWinHTTP(const std::wstring& url, const std::wstring& body)
{
    ParsedURL parsed = AutoUpdateParseUrl(url);
    // Parse host, port, path from url
    std::wstring hostStr = url;
    std::wstring path = L"/";
    int port = 80;
    bool https = false;

    if (hostStr.substr(0, 8) == L"https://") { hostStr = hostStr.substr(8); https = true; port = 443; }
    else if (hostStr.substr(0, 7) == L"http://") hostStr = hostStr.substr(7);

    size_t slash = hostStr.find(L'/');
    if (slash != std::wstring::npos) { path = hostStr.substr(slash); hostStr = hostStr.substr(0, slash); }
    size_t colon = hostStr.rfind(L':');
    if (colon != std::wstring::npos) {
        try { port = std::stoi(hostStr.substr(colon + 1)); } catch (...) {}
        hostStr = hostStr.substr(0, colon);
    }

    HINTERNET hSession = WinHttpOpen(L"OVS/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!hSession) { wprintf(L"[OVS] WinHTTP: WinHttpOpen failed (%lu)\n", GetLastError()); return false; }

    HINTERNET hConnect = WinHttpConnect(hSession, hostStr.c_str(), (INTERNET_PORT)port, 0);
    if (!hConnect) { wprintf(L"[OVS] WinHTTP: WinHttpConnect failed (%lu)\n", GetLastError()); WinHttpCloseHandle(hSession); return false; }

    DWORD flags = https ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"POST", path.c_str(), NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!hRequest) { wprintf(L"[OVS] WinHTTP: WinHttpOpenRequest failed (%lu)\n", GetLastError()); WinHttpCloseHandle(hConnect); WinHttpCloseHandle(hSession); return false; }

    DWORD timeout = 5000;
    WinHttpSetOption(hRequest, WINHTTP_OPTION_CONNECT_TIMEOUT, &timeout, sizeof(timeout));
    WinHttpSetOption(hRequest, WINHTTP_OPTION_SEND_TIMEOUT,    &timeout, sizeof(timeout));
    WinHttpSetOption(hRequest, WINHTTP_OPTION_RECEIVE_TIMEOUT, &timeout, sizeof(timeout));

    // Convert body to UTF-8 for transmission
    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, body.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string utf8Body(utf8Len - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, body.c_str(), -1, &utf8Body[0], utf8Len, nullptr, nullptr);

    BOOL ok = WinHttpSendRequest(hRequest, L"Content-Type: application/json\r\n", (DWORD)-1L,
        (LPVOID)utf8Body.c_str(), (DWORD)utf8Body.size(), (DWORD)utf8Body.size(), 0);

    if (!ok) wprintf(L"[OVS] WinHTTP: send failed (%lu)\n", GetLastError());
    else {
        WinHttpReceiveResponse(hRequest, NULL);
        wprintf(L"[OVS] WinHTTP: OK\n");
    }

    WinHttpCloseHandle(hRequest);
    WinHttpCloseHandle(hConnect);
    WinHttpCloseHandle(hSession);
    return ok;
}

// WinInet POST — works on Windows (native)
static bool PostViaWinInet(const std::wstring& url, const std::wstring& body)
{
    std::wstring hostStr = url;
    std::wstring path = L"/";
    int port = 80;

    if (hostStr.substr(0, 8) == L"https://") hostStr = hostStr.substr(8);
    else if (hostStr.substr(0, 7) == L"http://") hostStr = hostStr.substr(7);

    size_t slash = hostStr.find(L'/');
    if (slash != std::wstring::npos) { path = hostStr.substr(slash); hostStr = hostStr.substr(0, slash); }
    size_t colon = hostStr.rfind(L':');
    if (colon != std::wstring::npos) {
        try { port = std::stoi(hostStr.substr(colon + 1)); } catch (...) {}
        hostStr = hostStr.substr(0, colon);
    }

    HINTERNET hInet = InternetOpenW(L"OVS/1.0", INTERNET_OPEN_TYPE_DIRECT, NULL, NULL, 0);
    if (!hInet) { wprintf(L"[OVS] WinInet: InternetOpen failed (%lu)\n", GetLastError()); return false; }

    DWORD timeout = 5000;
    InternetSetOptionW(hInet, INTERNET_OPTION_CONNECT_TIMEOUT, &timeout, sizeof(timeout));
    InternetSetOptionW(hInet, INTERNET_OPTION_SEND_TIMEOUT,    &timeout, sizeof(timeout));
    InternetSetOptionW(hInet, INTERNET_OPTION_RECEIVE_TIMEOUT, &timeout, sizeof(timeout));

    HINTERNET hConn = InternetConnectW(hInet, hostStr.c_str(), (INTERNET_PORT)port, NULL, NULL, INTERNET_SERVICE_HTTP, 0, 0);
    if (!hConn) { wprintf(L"[OVS] WinInet: InternetConnect failed (%lu)\n", GetLastError()); InternetCloseHandle(hInet); return false; }

    HINTERNET hReq = HttpOpenRequestW(hConn, L"POST", path.c_str(), NULL, NULL, NULL, INTERNET_FLAG_NO_CACHE_WRITE | INTERNET_FLAG_RELOAD, 0);
    if (!hReq) { wprintf(L"[OVS] WinInet: HttpOpenRequest failed (%lu)\n", GetLastError()); InternetCloseHandle(hConn); InternetCloseHandle(hInet); return false; }

    // Convert body to UTF-8 for transmission
    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, body.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string utf8Body(utf8Len - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, body.c_str(), -1, &utf8Body[0], utf8Len, nullptr, nullptr);

    const char* headers = "Content-Type: application/json\r\n";
    BOOL ok = HttpSendRequestA(hReq, headers, (DWORD)strlen(headers), (LPVOID)utf8Body.c_str(), (DWORD)utf8Body.size());
    if (!ok) wprintf(L"[OVS] WinInet: send failed (%lu)\n", GetLastError());
    else     wprintf(L"[OVS] WinInet: OK\n");

    InternetCloseHandle(hReq);
    InternetCloseHandle(hConn);
    InternetCloseHandle(hInet);
    return ok;
}

// Find any loaded module that exports SteamAPI_ISteamUser_GetSteamID.
// On Proton the DLL may have a different name than steam_api64.dll.
static HMODULE FindSteamModule()
{
    // Try known names first
    const char* names[] = {
        "steam_api64.dll", "steamclient64.dll", "steamclient.dll",
        "steam_api.dll", "gameoverlayrenderer64.dll", nullptr
    };
    for (int i = 0; names[i]; i++) {
        HMODULE h = GetModuleHandleA(names[i]);
        if (h && GetProcAddress(h, "SteamAPI_ISteamUser_GetSteamID"))
            return h;
    }

    // Enumerate all loaded modules and look for the export
    HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, 0);
    if (hSnap == INVALID_HANDLE_VALUE) return nullptr;

    MODULEENTRY32 me = { sizeof(me) };
    if (Module32First(hSnap, &me)) {
        do {
            if (GetProcAddress(me.hModule, "SteamAPI_ISteamUser_GetSteamID")) {
                CloseHandle(hSnap);
                return me.hModule;
            }
        } while (Module32Next(hSnap, &me));
    }
    CloseHandle(hSnap);
    return nullptr;
}

// Try to read SteamID from loginusers.vdf using Proton env vars
static std::wstring ResolveSteamIDFromFile()
{
    std::vector<std::wstring> candidates;

    const char* compat = std::getenv("STEAM_COMPAT_CLIENT_INSTALL_PATH");
    if (compat && compat[0]) {
        std::wstring wCompat;
        int len = MultiByteToWideChar(CP_UTF8, 0, compat, -1, nullptr, 0);
        if (len > 0) {
            wCompat.resize(len - 1);
            MultiByteToWideChar(CP_UTF8, 0, compat, -1, &wCompat[0], len);
        }
        if (!wCompat.empty()) {
            candidates.push_back(wCompat + L"/config/loginusers.vdf");
            candidates.push_back(wCompat + L"\\config\\loginusers.vdf");
        }
    }

    const char* home = std::getenv("HOME");
    if (home && home[0]) {
        std::wstring wHome;
        int len = MultiByteToWideChar(CP_UTF8, 0, home, -1, nullptr, 0);
        if (len > 0) {
            wHome.resize(len - 1);
            MultiByteToWideChar(CP_UTF8, 0, home, -1, &wHome[0], len);
        }
        if (!wHome.empty()) {
            candidates.push_back(wHome + L"/.steam/steam/config/loginusers.vdf");
            candidates.push_back(wHome + L"/.local/share/Steam/config/loginusers.vdf");
        }
    }

    // Hardcoded Steam Deck default paths as last resort
    candidates.push_back(L"/home/deck/.steam/steam/config/loginusers.vdf");
    candidates.push_back(L"/home/deck/.local/share/Steam/config/loginusers.vdf");
    candidates.push_back(L"Z:\\home\\deck\\.steam\\steam\\config\\loginusers.vdf");
    candidates.push_back(L"Z:\\home\\deck\\.local\\share\\Steam\\config\\loginusers.vdf");

    for (const auto& path : candidates) {
        std::ifstream f(path);
        if (!f.is_open()) continue;

        std::string line, lastId, mostRecentId;

        while (std::getline(f, line)) {
            size_t start = line.find_first_not_of(" \t");
            if (start == std::string::npos) continue;
            std::string trimmed = line.substr(start);

            // SteamID64 key: quoted 15-20 digit number
            if (trimmed.size() > 2 && trimmed.front() == '"' && trimmed.back() == '"') {
                std::string key = trimmed.substr(1, trimmed.size() - 2);
                if (key.size() >= 15 && key.size() <= 20 &&
                    key.find_first_not_of("0123456789") == std::string::npos) {
                    lastId = key;
                }
            }
            if (!lastId.empty() && trimmed.find("\"MostRecent\"") != std::string::npos
                && trimmed.find("\"1\"") != std::string::npos)
                mostRecentId = lastId;
        }

        std::string result = mostRecentId.empty() ? lastId : mostRecentId;
        if (!result.empty()) {
            wprintf(L"[OVS] SteamID from file: %hs\n", result.c_str());
            // Convert result to wide string
            std::wstring wResult;
            int len = MultiByteToWideChar(CP_UTF8, 0, result.c_str(), -1, nullptr, 0);
            if (len > 0) {
                wResult.resize(len - 1);
                MultiByteToWideChar(CP_UTF8, 0, result.c_str(), -1, &wResult[0], len);
            }
            return wResult;
        }
    }
    return L"";
}

static std::wstring ResolveSteamIDFromAPI()
{
    typedef void*    (__cdecl* SteamAPI_SteamUser_t)();
    typedef uint64_t (__cdecl* SteamAPI_ISteamUser_GetSteamID_t)(void*);

    // Poll for up to 30s for steam_api64.dll
    HMODULE hSteam = nullptr;
    for (int i = 0; i < 60 && !hSteam; i++) {
        hSteam = FindSteamModule();
        if (!hSteam) Sleep(500);
    }

    if (hSteam) {
        auto pSteamUser  = (SteamAPI_SteamUser_t)            GetProcAddress(hSteam, "SteamAPI_SteamUser");
        auto pGetSteamID = (SteamAPI_ISteamUser_GetSteamID_t) GetProcAddress(hSteam, "SteamAPI_ISteamUser_GetSteamID");

        if (pSteamUser && pGetSteamID) {
            void* pUser = nullptr;
            for (int i = 0; i < 60 && !pUser; i++) { pUser = pSteamUser(); if (!pUser) Sleep(500); }
            if (pUser) {
                uint64_t id = pGetSteamID(pUser);
                if (id) {
                    wchar_t buf[32] = {};
                    swprintf_s(buf, L"%llu", (unsigned long long)id);
                    wprintf(L"[OVS] SteamID from API: %ls\n", buf);
                    return std::wstring(buf);
                }
            }
        }
    }

    // Fall back to loginusers.vdf
    return ResolveSteamIDFromFile();
}

// ============================================================
// Auto-update system — checks server for latest version,
// downloads new .asi, renames old one, kills game to apply.
// ============================================================

static wchar_t g_UpdateDownloadUrl[512] = {};
static wchar_t g_UpdateLatestVersion[64] = {};




static void DoPerformUpdate()
{
    wprintf(L"[AutoUpdate] Starting download from: %ls\n", g_UpdateDownloadUrl);

    wchar_t dllPath[MAX_PATH] = {};
    GetModuleFileNameW(HookMetadata::CurrentDllModule, dllPath, MAX_PATH);
    wprintf(L"[AutoUpdate] Current DLL: %s\n", dllPath);

    wchar_t tempDir[MAX_PATH] = {};
    GetTempPathW(MAX_PATH, tempDir);
    wchar_t tempFile[MAX_PATH] = {};
    swprintf_s(tempFile, MAX_PATH, L"%sOpenVersus_update.asi", tempDir);

    // Download via WinHTTP — works on both Windows and Proton/Steam Deck
    {
        wchar_t wHost[256] = {};
        wchar_t wPath[512] = {};
        INTERNET_PORT dlPort = 443;
        bool dlHttps = false;

        // Parse download URL into host/path/port
        std::wstring dlUrl = g_UpdateDownloadUrl;
        if (dlUrl.substr(0, 8) == L"https://") { dlHttps = true; dlUrl = dlUrl.substr(8); }
        else if (dlUrl.substr(0, 7) == L"http://") { dlUrl = dlUrl.substr(7); }
        size_t slash = dlUrl.find('/');
        std::wstring dlHost = (slash != std::wstring::npos) ? dlUrl.substr(0, slash) : dlUrl;
        std::wstring dlPathStr = (slash != std::wstring::npos) ? dlUrl.substr(slash) : L"/";
        size_t colon = dlHost.rfind(':');
        if (colon != std::wstring::npos) {
            try { dlPort = (INTERNET_PORT)std::stoi(dlHost.substr(colon + 1)); } catch (...) {}
            dlHost = dlHost.substr(0, colon);
        }
        //MultiByteToWideChar(CP_UTF8, 0, dlHost.c_str(), -1, wHost, 256);
        //MultiByteToWideChar(CP_UTF8, 0, dlPathStr.c_str(), -1, wPath, 512);

        HINTERNET hSess = WinHttpOpen(L"OVS/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
            WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
        if (!hSess) { wprintf(L"[AutoUpdate] WinHttpOpen failed (%lu)\n", GetLastError()); return; }

        HINTERNET hConn = WinHttpConnect(hSess, wHost, dlPort, 0);
        if (!hConn) { WinHttpCloseHandle(hSess); wprintf(L"[AutoUpdate] WinHttpConnect failed\n"); return; }

        DWORD flags = dlHttps ? WINHTTP_FLAG_SECURE : 0;
        HINTERNET hReq = WinHttpOpenRequest(hConn, L"GET", wPath, nullptr,
            WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
        if (!hReq) { WinHttpCloseHandle(hConn); WinHttpCloseHandle(hSess); wprintf(L"[AutoUpdate] WinHttpOpenRequest failed\n"); return; }

        if (!WinHttpSendRequest(hReq, WINHTTP_NO_ADDITIONAL_HEADERS, 0, nullptr, 0, 0, 0) ||
            !WinHttpReceiveResponse(hReq, nullptr)) {
            WinHttpCloseHandle(hReq); WinHttpCloseHandle(hConn); WinHttpCloseHandle(hSess);
            wprintf(L"[AutoUpdate] Download request failed (%lu)\n", GetLastError());
            return;
        }

        HANDLE hFile = CreateFileW(tempFile, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) {
            WinHttpCloseHandle(hReq); WinHttpCloseHandle(hConn); WinHttpCloseHandle(hSess);
            wprintf(L"[AutoUpdate] Failed to create temp file\n"); return;
        }

        DWORD totalWritten = 0;
        DWORD bytesAvail = 0;
        while (WinHttpQueryDataAvailable(hReq, &bytesAvail) && bytesAvail > 0) {
            std::vector<char> buf(bytesAvail);
            DWORD bytesRead = 0;
            if (WinHttpReadData(hReq, buf.data(), bytesAvail, &bytesRead) && bytesRead > 0) {
                DWORD written = 0;
                WriteFile(hFile, buf.data(), bytesRead, &written, nullptr);
                totalWritten += written;
            }
        }
        CloseHandle(hFile);
        WinHttpCloseHandle(hReq); WinHttpCloseHandle(hConn); WinHttpCloseHandle(hSess);
        wprintf(L"[AutoUpdate] Downloaded %lu bytes to %s\n", totalWritten, tempFile);
    }

    HANDLE hFile = CreateFileW(tempFile, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) {
        wprintf(L"[AutoUpdate] Downloaded file not found\n");
        return;
    }
    DWORD fileSize = GetFileSize(hFile, nullptr);
    CloseHandle(hFile);
    wprintf(L"[AutoUpdate] Downloaded %lu bytes\n", fileSize);

    if (fileSize < 10000) {
        wprintf(L"[AutoUpdate] File too small (%lu bytes), aborting\n", fileSize);
        DeleteFileW(tempFile);
        return;
    }

    wchar_t backupPath[MAX_PATH] = {};
    swprintf_s(backupPath, MAX_PATH, L"%s.bak", dllPath);
    DeleteFileW(backupPath);

    if (!MoveFileW(dllPath, backupPath)) {
        wprintf(L"[AutoUpdate] Failed to rename current DLL to .bak (error %lu)\n", GetLastError());
        DeleteFileW(tempFile);
        return;
    }
    wprintf(L"[AutoUpdate] Renamed current DLL to .bak\n");

    if (!MoveFileW(tempFile, dllPath)) {
        wprintf(L"[AutoUpdate] Failed to move new DLL into place (error %lu)\n", GetLastError());
        MoveFileW(backupPath, dllPath); // restore
        return;
    }

    wprintf(L"[AutoUpdate] New DLL installed! Restarting game...\n");
    Sleep(500);
    TerminateProcess(GetCurrentProcess(), 0);
}

static void CheckForUpdate()
{
    if (SettingsMgr->szServerUrl.empty()) return;

    std::wstring wUrl(SettingsMgr->szServerUrl.begin(), SettingsMgr->szServerUrl.end());
    ParsedURL parsed = OVS::Utils::AutoUpdateParseUrl(wUrl);
    if (!parsed.bParseSuccess) {
        wprintf(L"[AutoUpdate] Failed to parse server URL\n");
        return;
    }

    wchar_t path[256];
    swprintf_s(path, 256, L"/ovs/client-version?v=%s", OVS::OVS_Version);

    wchar_t responseBuf[2048];
    int bodyLen = OVS::Utils::AutoUpdateHttpRequest(parsed.Host.c_str(), _wtoi(parsed.Port.c_str()), path, responseBuf, sizeof(responseBuf));
    if (bodyLen <= 0) {
        wprintf(L"[AutoUpdate] Version check failed (no response)\n");
        return;
    }

    wchar_t latestVersion[64] = {};
    wchar_t downloadUrl[512] = {};
    OVS::Utils::AutoUpdateJsonGetString(responseBuf, L"latest_version", latestVersion, sizeof(latestVersion));
    OVS::Utils::AutoUpdateJsonGetString(responseBuf, L"download_url",   downloadUrl,   sizeof(downloadUrl));

    wprintf(L"[AutoUpdate] Current: %ls, Latest: %ls\n", OVS::OVS_Version, latestVersion);

    if (wcsstr(responseBuf, L"\"is_latest\":true") || wcsstr(responseBuf, L"\"is_latest\": true")) {
        wprintf(L"[AutoUpdate] Already up to date\n");
        return;
    }

    if (latestVersion[0] == '\0' || downloadUrl[0] == '\0') {
        wprintf(L"[AutoUpdate] Missing version/URL in response, skipping\n");
        return;
    }

    wcsncpy_s(g_UpdateLatestVersion, latestVersion, sizeof(g_UpdateLatestVersion) - 1);
    wcsncpy_s(g_UpdateDownloadUrl,   downloadUrl,   sizeof(g_UpdateDownloadUrl)   - 1);

    wprintf(L"[AutoUpdate] Update available (%s) — downloading automatically...\n", latestVersion);
    DoPerformUpdate();
}

static DWORD WINAPI CheckForUpdateThread(LPVOID)
{
    Sleep(5000); // wait for game to settle before checking
    CheckForUpdate();
    return 0;
}

// ============================================================

static void RegisterIdentity()
{
    if (!GEnvInfo) return;
    if (SettingsMgr->szServerUrl.empty()) return;

    // If SteamID wasn't set from env vars (Proton doesn't inject it),
    // resolve it from the Steam API after SteamAPI_Init() completes.
    if (GEnvInfo->SteamID == L"Unknown" || GEnvInfo->SteamID.empty()) {
        std::wstring apiId = ResolveSteamIDFromAPI();
        if (!apiId.empty()) {
            GEnvInfo->SteamID = apiId;
            GEnvInfo->IsSteam = true;
        }
    }

    std::wstring url = SettingsMgr->szServerUrl;
    if (!url.empty() && url.back() == L'/') url.pop_back();
    url += L"/api/identify";

    StringBuilder sb = L"{\"steamId\":\"";
    sb.Append(GEnvInfo->SteamID)
        .Append(L"\",\"epicId\":\"")
        .Append(GEnvInfo->EpicID)
        .Append(L"\",\"hardwareId\":\"")
        .Append(GEnvInfo->HardwareID)
        .Append(L"\",\"clientVersion\":\"")
        .Append(OVS::OVS_Version)
        .Append(L"\"}");

    //std::wstring body = L"{\"steamId\":\"" + GEnvInfo->SteamID
    //    + L"\",\"epicId\":\"" + GEnvInfo->EpicID
    //    + L"\",\"hardwareId\":\"" + GEnvInfo->HardwareID
    //    + L"\",\"clientVersion\":\"" + std::wstring(OVS::OVS_Version) + L"\"}";

    std::wstring body = sb.ToString();

    wprintf(L"Will send body to OVS server: %ls\n", body.c_str());

    if (SettingsMgr->bDebug)
    {
        GEnvInfo->Print();
    }

    wprintf(L"[OVS] RegisterIdentity: %ls\n", body.c_str());

    bool ok = IsProton() ? PostViaWinHTTP(url, body) : PostViaWinInet(url, body);

    if (ok) wprintf(L"[OVS] RegisterIdentity: done\n");
    else    wprintf(L"[OVS] RegisterIdentity: failed\n");
}

static DWORD WINAPI RegisterIdentityThread(LPVOID)
{
    RegisterIdentity();
    return 0;
}

bool OnInitializeHook()
{
    FirstRunMgr->Init();
    SettingsMgr->Init();

    if (!SettingsMgr->bAllowNonMVS && !(VerifyProcessName(L"MultiVersus-Win64-Shipping.exe") || VerifyProcessName(L"MultiVersus.exe") || VerifyProcessName(L"OVS.exe")))
    {
        SpawnError(L"OVS only works with the original MVS steam game! Don't be surprised if it crashes.");
        //return false;
    }

    if (!HandleWindowsVersion())
    {
        return false;
    }
    if (SettingsMgr->bEnableConsoleWindow)
    {
        CreateConsole();
    }

    if (SettingsMgr->bEnableKeyboardHotkeys)
    {
        if (!(HookMetadata::KeyboardProcHook = SetWindowsHookEx(WH_KEYBOARD, KeyboardProc, HookMetadata::CurrentDllModule, GetCurrentThreadId())))
        {
            wchar_t x[100];
            wsprintfW(x, L"Failed To Hook Keyboard FN: 0x%X", GetLastError());
            MessageBoxW(NULL, x, L"Error", MB_ICONERROR);
        }
    }

    if (SettingsMgr->bPauseOnStart)
    {
        MessageBoxW(0, L"Freezing Game Until OK", L":)", MB_ICONINFORMATION);
    }

    // Collect Steam/Epic identity and hardware fingerprint
    GEnvInfo = new EnvInfo();

    // Hash Exe
    uint64_t EXEHash = HashTextSectionOfHost();
    // Convert version to narrow string for CachedPatternsMgr
    int versionLen = WideCharToMultiByte(CP_UTF8, 0, CURRENT_HOOK_VERSION.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string narrowVersion;
    if (versionLen > 0) {
        narrowVersion.resize(versionLen - 1);
        WideCharToMultiByte(CP_UTF8, 0, CURRENT_HOOK_VERSION.c_str(), -1, &narrowVersion[0], versionLen, nullptr, nullptr);
    }
    CachedPatternsMgr->Init(EXEHash, narrowVersion.c_str());

    ProcessSettings(); // Parse Settings
    PreGameHooks(); // Queue Blocker
    SpawnP2PServer();

    // Register identity with OVS server — runs on a separate thread so it never blocks game launch
    CreateThread(nullptr, 0, RegisterIdentityThread, nullptr, 0, nullptr);

    // Check for DLL updates from the OVS server
    CreateThread(nullptr, 0, CheckForUpdateThread, nullptr, 0, nullptr);

    return true;
}

static void OnShutdown()
{
    if (HookMetadata::KeyboardProcHook) // Will be unloaded once by DLL, and once by EHP.
    {
        UnhookWindowsHookEx(HookMetadata::KeyboardProcHook);
        HookMetadata::KeyboardProcHook = nullptr;
    }

    DespawnP2PServer();
}

// Dll Entry

bool APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpRes)
{
    HookMetadata::CurrentDllModule = hModule;
    HHOOK hook_ = nullptr;
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        printfInfo(L"On Attach Initialize");
        OnInitializeHook();
        InitializeKeyboard();
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        OnShutdown();
        break;
    }
    return true;
}
