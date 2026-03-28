#include "includes.h"
#include "src/OVSUtils.h"
#include "src/mvs.h"
#include "src/OpenVersus.h"
#include "src/EnvInfo.h"
#include <tlhelp32.h>
#include <VersionHelpers.h>
#include <string>
#include <iostream>
#include <filesystem>
#include <fstream>
#include <winhttp.h>
#include <wininet.h>
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "wininet.lib")

namespace fs = std::filesystem;

constexpr const char * CURRENT_HOOK_VERSION = "2026.02.02";
const char* OVS::OVS_Version = (const char*)CURRENT_HOOK_VERSION;

EnvInfo* GEnvInfo = nullptr;


Trampoline* GameTramp, * User32Tramp;

void CreateConsole();
void SpawnError(const char*);
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
            printf("Attempting F1 action\n");
            //OVS::ShowNotification("OVS Loaded", CURRENT_HOOK_VERSION, 10.f, true);
            //RunDumper();
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

    std::string consoleName = "OpenVersus Debug Console";
    SetConsoleTitleA(consoleName.c_str());
    HookMetadata::Console = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD dwMode = 0;
    GetConsoleMode(HookMetadata::Console, &dwMode);
    dwMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
    SetConsoleMode(HookMetadata::Console, dwMode);

    printfCyan("OpenVersus - It's better than Parsec\n");
    printfCyan("v%s\n\n", CURRENT_HOOK_VERSION);
    printfCyan("Available at: https://github.com/christopher-conley/OpenVersus\n");
    printfCyan("Maintained by RosettaSt0ned and the MVS community\n");

    printfCyan("OpenVersus is originally based on the publicly-available code developed by thethiny and multiversuskoth, located at: \n");
    printfCyan("https://github.com/thethiny/MVSIASI\n");
    printfCyan("https://github.com/multiversuskoth/mvs-http-server\n");
    printfCyan("https://github.com/multiversuskoth/mvs-udp-server\n");

}

void PreGameHooks()
{
    GameTramp = Trampoline::MakeTrampoline(GetModuleHandle(nullptr));
    if (SettingsMgr->iLogLevel)
        printf("Generated Trampolines\n");
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
    if ((SettingsMgr->bLoadDumper) && (SettingsMgr->bRunDumperOnLoad))
    {
        HookMetadata::sActiveMods.bLoadDumper = true;
        HookMetadata::sActiveMods.bRunDumperOnLoad = true;
        RunDumper();

        bool DoneFileExists = false;
        while (!DoneFileExists)
        {
            DoneFileExists = fs::exists("C:\\Users\\tool\\dump_done.txt");
        }
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

    printfCyan("Parsed Settings\n");
}

void SpawnError(const char* msg)
{
    MessageBoxA(NULL, msg, "OpenVersus", MB_ICONEXCLAMATION);
}

bool HandleWindowsVersion()
{
    if (IsWindows10OrGreater())
    {
        return true;
    }

    if (IsWindows7SP1OrGreater())
    {
        SpawnError("OVS doesn't officially support Windows 8 or 7 SP1. It may misbehave.");
        return true;
    }

    SpawnError("OVS doesn't support Windows 7 or Earlier. Might not work.");
    return true;


}

inline bool VerifyProcessName(std::string expected_process) {
    std::string process_name = GetProcessName();

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
static bool PostViaWinHTTP(const std::string& url, const std::string& body)
{
    // Parse host, port, path from url
    std::string hostStr = url;
    std::string path = "/";
    int port = 80;
    bool https = false;

    if (hostStr.substr(0, 8) == "https://") { hostStr = hostStr.substr(8); https = true; port = 443; }
    else if (hostStr.substr(0, 7) == "http://") hostStr = hostStr.substr(7);

    size_t slash = hostStr.find('/');
    if (slash != std::string::npos) { path = hostStr.substr(slash); hostStr = hostStr.substr(0, slash); }
    size_t colon = hostStr.rfind(':');
    if (colon != std::string::npos) {
        try { port = std::stoi(hostStr.substr(colon + 1)); } catch (...) {}
        hostStr = hostStr.substr(0, colon);
    }

    std::wstring wHost(hostStr.begin(), hostStr.end());
    std::wstring wPath(path.begin(), path.end());

    HINTERNET hSession = WinHttpOpen(L"OVS/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!hSession) { printf("[OVS] WinHTTP: WinHttpOpen failed (%lu)\n", GetLastError()); return false; }

    HINTERNET hConnect = WinHttpConnect(hSession, wHost.c_str(), (INTERNET_PORT)port, 0);
    if (!hConnect) { printf("[OVS] WinHTTP: WinHttpConnect failed (%lu)\n", GetLastError()); WinHttpCloseHandle(hSession); return false; }

    DWORD flags = https ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"POST", wPath.c_str(), NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!hRequest) { printf("[OVS] WinHTTP: WinHttpOpenRequest failed (%lu)\n", GetLastError()); WinHttpCloseHandle(hConnect); WinHttpCloseHandle(hSession); return false; }

    DWORD timeout = 5000;
    WinHttpSetOption(hRequest, WINHTTP_OPTION_CONNECT_TIMEOUT, &timeout, sizeof(timeout));
    WinHttpSetOption(hRequest, WINHTTP_OPTION_SEND_TIMEOUT,    &timeout, sizeof(timeout));
    WinHttpSetOption(hRequest, WINHTTP_OPTION_RECEIVE_TIMEOUT, &timeout, sizeof(timeout));

    BOOL ok = WinHttpSendRequest(hRequest, L"Content-Type: application/json\r\n", (DWORD)-1L,
        (LPVOID)body.c_str(), (DWORD)body.size(), (DWORD)body.size(), 0);

    if (!ok) printf("[OVS] WinHTTP: send failed (%lu)\n", GetLastError());
    else {
        WinHttpReceiveResponse(hRequest, NULL);
        printf("[OVS] WinHTTP: OK\n");
    }

    WinHttpCloseHandle(hRequest);
    WinHttpCloseHandle(hConnect);
    WinHttpCloseHandle(hSession);
    return ok;
}

// WinInet POST — works on Windows (native)
static bool PostViaWinInet(const std::string& url, const std::string& body)
{
    std::string hostStr = url;
    std::string path = "/";
    int port = 80;

    if (hostStr.substr(0, 8) == "https://") hostStr = hostStr.substr(8);
    else if (hostStr.substr(0, 7) == "http://") hostStr = hostStr.substr(7);

    size_t slash = hostStr.find('/');
    if (slash != std::string::npos) { path = hostStr.substr(slash); hostStr = hostStr.substr(0, slash); }
    size_t colon = hostStr.rfind(':');
    if (colon != std::string::npos) {
        try { port = std::stoi(hostStr.substr(colon + 1)); } catch (...) {}
        hostStr = hostStr.substr(0, colon);
    }

    HINTERNET hInet = InternetOpenA("OVS/1.0", INTERNET_OPEN_TYPE_DIRECT, NULL, NULL, 0);
    if (!hInet) { printf("[OVS] WinInet: InternetOpen failed (%lu)\n", GetLastError()); return false; }

    DWORD timeout = 5000;
    InternetSetOptionA(hInet, INTERNET_OPTION_CONNECT_TIMEOUT, &timeout, sizeof(timeout));
    InternetSetOptionA(hInet, INTERNET_OPTION_SEND_TIMEOUT,    &timeout, sizeof(timeout));
    InternetSetOptionA(hInet, INTERNET_OPTION_RECEIVE_TIMEOUT, &timeout, sizeof(timeout));

    HINTERNET hConn = InternetConnectA(hInet, hostStr.c_str(), (INTERNET_PORT)port, NULL, NULL, INTERNET_SERVICE_HTTP, 0, 0);
    if (!hConn) { printf("[OVS] WinInet: InternetConnect failed (%lu)\n", GetLastError()); InternetCloseHandle(hInet); return false; }

    HINTERNET hReq = HttpOpenRequestA(hConn, "POST", path.c_str(), NULL, NULL, NULL, INTERNET_FLAG_NO_CACHE_WRITE | INTERNET_FLAG_RELOAD, 0);
    if (!hReq) { printf("[OVS] WinInet: HttpOpenRequest failed (%lu)\n", GetLastError()); InternetCloseHandle(hConn); InternetCloseHandle(hInet); return false; }

    const char* headers = "Content-Type: application/json\r\n";
    BOOL ok = HttpSendRequestA(hReq, headers, (DWORD)strlen(headers), (LPVOID)body.c_str(), (DWORD)body.size());
    if (!ok) printf("[OVS] WinInet: send failed (%lu)\n", GetLastError());
    else     printf("[OVS] WinInet: OK\n");

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
static std::string ResolveSteamIDFromFile()
{
    std::vector<std::string> candidates;

    const char* compat = std::getenv("STEAM_COMPAT_CLIENT_INSTALL_PATH");
    if (compat && compat[0]) {
        candidates.push_back(std::string(compat) + "/config/loginusers.vdf");
        candidates.push_back(std::string(compat) + "\\config\\loginusers.vdf");
    }

    const char* home = std::getenv("HOME");
    if (home && home[0]) {
        candidates.push_back(std::string(home) + "/.steam/steam/config/loginusers.vdf");
        candidates.push_back(std::string(home) + "/.local/share/Steam/config/loginusers.vdf");
    }

    // Hardcoded Steam Deck default paths as last resort
    candidates.push_back("/home/deck/.steam/steam/config/loginusers.vdf");
    candidates.push_back("/home/deck/.local/share/Steam/config/loginusers.vdf");
    candidates.push_back("Z:\\home\\deck\\.steam\\steam\\config\\loginusers.vdf");
    candidates.push_back("Z:\\home\\deck\\.local\\share\\Steam\\config\\loginusers.vdf");

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
            printf("[OVS] SteamID from file: %s\n", result.c_str());
            return result;
        }
    }
    return "";
}

static std::string ResolveSteamIDFromAPI()
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
                    char buf[32] = {};
                    sprintf_s(buf, "%llu", (unsigned long long)id);
                    printf("[OVS] SteamID from API: %s\n", buf);
                    return std::string(buf);
                }
            }
        }
    }

    // Fall back to loginusers.vdf
    return ResolveSteamIDFromFile();
}

static void RegisterIdentity()
{
    if (!GEnvInfo) return;
    if (SettingsMgr->szServerUrl.empty()) return;

    // If SteamID wasn't set from env vars (Proton doesn't inject it),
    // resolve it from the Steam API after SteamAPI_Init() completes.
    if (GEnvInfo->SteamID == "Unknown" || GEnvInfo->SteamID.empty()) {
        std::string apiId = ResolveSteamIDFromAPI();
        if (!apiId.empty()) {
            GEnvInfo->SteamID = apiId;
            GEnvInfo->IsSteam = true;
        }
    }

    std::string hardwareId = GEnvInfo->HardwareID;

    std::string url = SettingsMgr->szServerUrl;
    if (!url.empty() && url.back() == '/') url.pop_back();
    url += "/api/identify";

    std::string body = "{\"steamId\":\"" + GEnvInfo->SteamID
        + "\",\"epicId\":\"" + GEnvInfo->EpicID
        + "\",\"hardwareId\":\"" + hardwareId + "\"}";

    printf("[OVS] RegisterIdentity: %s\n", body.c_str());

    bool ok = IsProton() ? PostViaWinHTTP(url, body) : PostViaWinInet(url, body);

    if (ok) printf("[OVS] RegisterIdentity: done\n");
    else    printf("[OVS] RegisterIdentity: failed\n");
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

    if (!SettingsMgr->bAllowNonMVS && !(VerifyProcessName("MultiVersus-Win64-Shipping.exe") || VerifyProcessName("MultiVersus.exe") || VerifyProcessName("OVS.exe")))
    {
        SpawnError("OVS only works with the original MVS steam game!");
        return false;
    }

    if (!HandleWindowsVersion())
        return false;

    if (SettingsMgr->bEnableConsoleWindow)
    {
        CreateConsole();
    }

    if (SettingsMgr->bEnableKeyboardHotkeys)
    {
        if (!(HookMetadata::KeyboardProcHook = SetWindowsHookEx(WH_KEYBOARD, KeyboardProc, HookMetadata::CurrentDllModule, GetCurrentThreadId())))
        {
            char x[100];
            sprintf(x, "Failed To Hook Keyboard FN: 0x%X", GetLastError());
            MessageBox(NULL, x, "Error", MB_ICONERROR);
        }
    }

    if (SettingsMgr->bPauseOnStart)
    {
        MessageBoxA(0, "Freezing Game Until OK", ":)", MB_ICONINFORMATION);
    }

    // Collect Steam/Epic identity and hardware fingerprint
    GEnvInfo = new EnvInfo();

    // Hash Exe
    uint64_t EXEHash = HashTextSectionOfHost();
    CachedPatternsMgr->Init(EXEHash, CURRENT_HOOK_VERSION);

    ProcessSettings(); // Parse Settings
    PreGameHooks(); // Queue Blocker
    SpawnP2PServer();

    // Register identity with OVS server — runs on a separate thread so it never blocks game launch
    CreateThread(nullptr, 0, RegisterIdentityThread, nullptr, 0, nullptr);

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
        printfInfo("On Attach Initialize");
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
