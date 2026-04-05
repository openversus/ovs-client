#include "constants.hpp"
#include "utils/Utils.hpp"
#include "ovs/OpenVersus.h"
#include "ovs/OVSUtils.h"
#include "utils/prettyprint.h"
#include <string>
#include <ostream>


HookMetadata::ActiveMods			HookMetadata::sActiveMods;
HookMetadata::LibMapsStruct			HookMetadata::sLFS;
bool OVS::IsOVSUpdateRequired = false;

OVS::OVSSetting OVS::OVSDefaultSettingsArray[] = {
    // Debug Settings
    { L"bEnableConsoleWindow", L"Settings.Debug", L"ShowConsole", L"bool", true },
    { L"bPauseOnStart", L"Settings.Debug", L"DebugPause", L"bool", false },
    { L"bDebug", L"Settings.Debug", L"DebugLogging", L"bool", false },
    { L"bAllowNonMVS", L"Settings.Debug", L"NonMVSPatching", L"bool", false },
    // Settings
    { L"iLogSize", L"Settings", L"LogSize", L"int", (size_t)50, true },
    { L"iLogLevel", L"Settings", L"LogLevel", L"int", (size_t)0, true },
    { L"szModLoader", L"Settings", L"ModLoader", L"string", L"Kernel32.CreateFileW" },
    { L"szAntiCheatEngine", L"Settings", L"AntiCheatEngine", L"string", L"User32.EnumChildWindows" },
    { L"szCurlSetOpt", L"Settings", L"CurlSetOpt", L"string", L"libcurl.curl_easy_setopt" },
    { L"szCurlPerform", L"Settings", L"CurlPerform", L"string", L"libcurl.curl_easy_perform" },
    { L"bEnableKeyboardHotkeys", L"Settings", L"EnableKeyboardHotkeys", L"booL", true },
    // Keybinds
    { L"hkMenu", L"Settings.Keybinds", L"ToggleMenu", L"string", L"F1" },
    // Patches
    { L"bSunsetDate", L"Patches", L"SunsetDate", L"booL", true },
    { L"bDisableSignatureCheck", L"Patches", L"PakLoader", L"booL", true },
    // Features
    { L"bHookUE", L"Features", L"HookUE", L"booL", true },
    { L"bDialog", L"Features", L"Dialog", L"booL", true },
    { L"bNotifs", L"Features", L"Notifications", L"booL", true },
    { L"pSigCheck", L"Patterns", L"SigCheck", L"string", OVS::Patterns::SigCheck },
    { L"pEndpointLoader", L"Patterns", L"EndpointLoader", L"string", OVS::Patterns::EndpointLoader },
    { L"pProdEndpointLoader", L"Patterns", L"ProdEndpointLoader", L"string", OVS::Patterns::ProdEndpointLoader },
    { L"pSunsetDate", L"Patterns", L"SunsetDate", L"string", OVS::Patterns::SunsetDate },
    { L"pFText", L"Patterns.UE", L"FText", L"string", OVS::Patterns::FText },
    { L"pCFName", L"Patterns.UE", L"CFName", L"string", OVS::Patterns::CFName },
    { L"pWCFname", L"Patterns.UE", L"WCFName", L"string", OVS::Patterns::WCFName },
    { L"pDialog", L"Patterns.MVS", L"Dialog", L"string", OVS::Patterns::Dialog },
    { L"pDialogParams", L"Patterns.MVS", L"DialogParams", L"string", OVS::Patterns::DialogParams },
    { L"pDialogCallback", L"Patterns.MVS", L"DialogCallback", L"string", OVS::Patterns::DialogCallback },
    { L"pQuitGameCallback", L"Patterns.MVS", L"QuitGameCallback", L"string", OVS::Patterns::QuitGameCallback },
    { L"pFighterInstance", L"Patterns.MVS", L"FighterInstance", L"string", OVS::Patterns::FighterInstance },
    { L"pNotifs", L"Patterns.MVS", L"Notifications", L"string", OVS::Patterns::Notifications },
    { L"szServerUrl", L"Server.Game", L"ServerUrl", L"string", OVS::ServerUrl },
    { L"szProdServerUrl", L"Server.Prod", L"ServerUrl", L"string", OVS::ProdServerUrl },
    { L"bEnableServerProxy", L"Server.Game", L"Enabled", L"bool", true },
    { L"bEnableProdServerProxy", L"Server.Prod", L"Enabled", L"bool", true }
};

const size_t OVS::OVSDefaultSettingsArrayCount = sizeof(OVS::OVSDefaultSettingsArray) / sizeof(OVS::OVSDefaultSettingsArray[0]);

namespace OVS
{
    const wchar_t* WDEBUG_PREFIX = L"\033[0m[\x1b[33mDBG\033[0m] ";
    const wchar_t* WINFO_PREFIX = L"\033[0m[\x1b[32mNFO\033[0m] ";
    const wchar_t* WWARN_PREFIX = L"\033[0m[\x1b[33mWRN\033[0m] ";
    const wchar_t* WERROR_PREFIX = L"\033[0m[\x1b[31mERR\033[0m] ";
    const wchar_t* WCRITICAL_PREFIX = L"\033[0m[\x1b[31m\x1b[5mCRT\x1b[25m\033[0m] ";

    std::map<LogLevel, std::wstring> LogPrefixMap = {
        { LOG_DEBUG, WDEBUG_PREFIX },
        { LOG_INFO, WINFO_PREFIX },
        { LOG_WARN, WWARN_PREFIX },
        { LOG_ERROR, WERROR_PREFIX },
        { LOG_CRITICAL, WCRITICAL_PREFIX }
    };

    std::map<LogLevel, std::wostream*> LogStreams = {
        { LOG_DEBUG, &std::wclog },
        { LOG_INFO, &std::wcout },
        { LOG_WARN, &std::wcerr },
        { LOG_ERROR, &std::wcerr },
        { LOG_CRITICAL, &std::wcerr }
    };

    OVSSetting GetOVSSettingByName(const wchar_t* Name)
    {
        for (size_t i = 0; i < OVSDefaultSettingsArrayCount; i++)
        {
            if (!wcscmp(OVSDefaultSettingsArray[i].Name, Name))
            {
                return OVSDefaultSettingsArray[i];
            }
        }

        OVSSetting notFoundSetting = { L"", L"", L"", L"", L"" };
        return notFoundSetting;
    }
}

namespace OVS::Proxies {

    HANDLE __stdcall CreateFile(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode, LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition, DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
    {
        //if (lpFileName)
        //{
        //	//std::wstring fileName = lpFileName;
        //	wchar_t* wcFileName = (wchar_t*)lpFileName;
        //	std::wstring fileName(wcFileName, wcslen(wcFileName));
        //	if (!wcsncmp(wcFileName, L"..", 2))
        //	{
        //		std::wstring wsSwapFolder = L"MKSwap";
        //		std::wstring newFileName = L"..\\" + wsSwapFolder + fileName.substr(2, fileName.length() - 2);
        //		if (std::filesystem::exists(newFileName.c_str()))
        //		{
        //			wprintf(L"Loading %s from %s\n", wcFileName, wsSwapFolder.c_str());
        //			MVSGame::vSwappedFiles.push_back(wcFileName);
        //			return ::CreateFileW(newFileName.c_str(), dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
        //		}
        //	}

        //}

        return ::CreateFileW(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
    }

    void __cdecl DummyPrint(uint64_t* a) {
        wprintf(L"DUMMY PRINT! %p\n", a);
    }

    void __cdecl TestCallback(uint64_t* a) {
        wprintf(L"Test Callback! %p\n", a);
    }

    MVSGame::UFighterGameInstance* CopyFighterInstance(MVSGame::UFighterGameInstance* t, uint64_t* a2)
    {
        MVSGame::Instances::FighterGame = t;
        return MVSGame::UFigherGameInstanceConst(t, a2);
    }

    void SaveModWarningState()
    {
        FirstRunMgr->bPaidModWarned = true;
        FirstRunMgr->Save();
    }

    void OfflineModeWarning()
    {
        ShowNotification(L"Update Required", L"Offline mode activated", 10.f);
    }

    bool OVSOfflineModeChecker(int32_t* _TSS0_395)
    {
        static bool isUpdateWarned = !SettingsMgr->bDebug; // Only for debug now
        static bool isPaidWarnShown = FirstRunMgr->bPaidModWarned;
        static bool isVersionShown = false;
        MVSGame::Init_thread_header(_TSS0_395);
        if (_TSS0_395[0] == -1) // This segment will only be called once
        {
            // Check for updates

                
            //MVSGame::FDateTime(MVSGame::kSunsetDate, 2025, 6, 1, 0, 0, 0, 0); // No longer needed
            OVS::IsOVSUpdateRequired = false;

            MVSGame::Init_thread_footer(_TSS0_395);
        }

        if (!isPaidWarnShown && MVSGame::Instances::FighterGame)
        {

            MVSGame::UMvsDialog* dialog = ShowDialog((const char*)"OpenVersus is a FREE mod!", (const char*)"If you have paid for this, please ask for a refund!", (const char*)"I Agree", (const char*)"I Disagree");
            if (dialog)
            {
                uint64_t resultYes = dialog->AssignCallbackToButton(&dialog->OnButtonOneClicked, &SaveModWarningState);
                uint64_t resultNo = dialog->AssignCallbackToButton(&dialog->OnButtonTwoClicked, MVSGame::QuitGame);

                isPaidWarnShown = true;
            }
        }

        if (!isVersionShown && MVSGame::Instances::FighterGame)
        {
            if (ShowNotification(L"OpenVersus Loaded", OVS_Version, 05.f))
                isVersionShown = true;
        }

        if (false)
        //if (!isUpdateWarned && MVSGame::Instances::FighterGame)
        {
            MVSGame::UMvsDialog* dialog = ShowDialog((const char*)"New Update Available!", (const char*)"Would you like to download the update now?", (const char*)"Yes", (const char*)"Play Offline");
            if (dialog)
            {
                uint64_t resultYes = dialog->AssignCallbackToButton(&dialog->OnButtonOneClicked, MVSGame::QuitGame); // Change to the update one
                uint64_t resultNo = dialog->AssignCallbackToButton(&dialog->OnButtonTwoClicked, &OfflineModeWarning); // Create function to disable internet or simply refuse connections from the server.
                isUpdateWarned = true;
            }
        }
        
        //return OVS::IsOVSUpdateRequired; // Other checks can also be placed here
        return false;
    }

    int64_t* OverrideProdEndpoint(int64_t* FStringPtr, const wchar_t* EndpointUrl)
    {
        std::wstring ServerUrl = SettingsMgr->szProdServerUrl;

        if (HookMetadata::sActiveMods.bProdEndpointSwap && !ServerUrl.empty())
        {
            // Strip trailing slash and add it back
            if (!ServerUrl.empty() && ServerUrl.back() == L'/') {
                ServerUrl.pop_back();
            }
            ServerUrl += L"/";

            SetColorCyan();
            wprintf(L"Rerouting traffic from vanilla Prod HTTP/WS server \"%ls\" to \"%ls\"!\n", EndpointUrl, ServerUrl.c_str());
            ResetColors();

            const wchar_t* end = ServerUrl.c_str();

            MVSGame::SetFStringValue(FStringPtr, end);

        }
        else
        {
            MVSGame::SetFStringValue(FStringPtr, EndpointUrl);
        }

        return FStringPtr;

    }

    const char** OverrideGameEndpoint(int64_t* TargetStringDest, const wchar_t * EndpointAddress)
    {
        std::wstring ServerUrl = SettingsMgr->szServerUrl;

        if (HookMetadata::sActiveMods.bGameEndpointSwap && !ServerUrl.empty())
        {
            // Convert wide string to narrow for endpoint processing
            //int narrowLen = WideCharToMultiByte(CP_UTF8, 0, ServerUrl.c_str(), -1, nullptr, 0, nullptr, nullptr);
            //std::string narrowServerUrl;
            //if (narrowLen > 0) {
            //    narrowServerUrl.resize(narrowLen - 1);
            //    WideCharToMultiByte(CP_UTF8, 0, ServerUrl.c_str(), -1, &narrowServerUrl[0], narrowLen, nullptr, nullptr);
            //}

            // Strip trailing slash
            //if (!narrowServerUrl.empty() && narrowServerUrl.back() == '/') {
            //    narrowServerUrl.pop_back();
            //}

            if (ServerUrl.empty() && ServerUrl.back() == '/')
            {
                ServerUrl.pop_back();
            }

            SetColorCyan();
            wprintf(L"Rerouting traffic from vanilla HTTP/WS server \"%llx\" to \"%ls\"!\n", (int64_t)EndpointAddress, ServerUrl.c_str());
            ResetColors();

            const char* end = OVS::Utils::ToNarrowStr(ServerUrl.c_str()).c_str();
            //const char* end =  ServerUrl.c_str();

            MVSGame::GetEndpointKeyValue(TargetStringDest, end);
            return (const char**)TargetStringDest;
        }

        const char* end2 = OVS::Utils::ToNarrowStr(EndpointAddress).c_str();
        return MVSGame::GetEndpointKeyValue(TargetStringDest, end2);
        //return MVSGame::GetEndpointKeyValue(TargetStringDest, EndpointAddress);

    }
};

std::map<int, const char*> CURL_MAP
{
    {46,	"CURLOPT_UPLOAD"},
    {47,	"CURLOPT_POST"},
    {10001,	"CURLOPT_WRITEDATA"},
    {10029,	"CURLOPT_HEADERDATA"},
    {10002,	"CURLOPT_URL"},
    {10004,	"CURLOPT_PROXY"},
    {10173,	"CURLOPT_USERNAME"},
    {10005,	"CURLOPT_USERPWD"},
    {10023,	"CURLOPT_HTTPHEADER"},
    {60,	"CURLOPT_POSTFIELDSIZE"},
    {20012,	"CURLOPT_READFUNCTION"},
    {10009,	"CURLOPT_READDATA"},
    {10010,	"CURLOPT_ERRORBUFFER"},
    {8,		"CURLPROTO_FTPS"},
    {10015,	"CURLOPT_POSTFIELDS"},
    {20011,	"CURLOPT_WRITEFUNCTION"},
    {10018,	"CURLOPT_USERAGENT"},
    {80,	"CURLOPT_HTTPGET"},
    {81,	"CURLOPT_SSL_VERIFYHOST"},
    {14,	"CURLOPT_INFILESIZE"},
    {64,	"CURLOPT_SSL_VERIFYPEER"},
    {99,	"CURLOPT_NOSIGNAL"},
    {41,	"CURLOPT_VERBOSE"},
    {42,	"CURLOPT_HEADER"},
    {43,	"CURLOPT_NOPROGRESS"},
    {10086,	"CURLOPT_SSLCERTTYPE"},
    {20079, "CURLOPT_HEADERFUNCTION"},
    {20108, "CURLOPT_SSL_CTX_FUNCTION"},
    {10065, "CURLOPT_CAINFO"},
    {10097, "CURLOPT_CAPATH"},

};

bool bFirstPost = true;

enum class ArgTypes {
    ARGTYPE_NONE = 0, // UNK
    ARGTYPE_STRING, // String Pointer
    ARGTYPE_AGBINARY, // Data
    ARGTYPE_FUNCTION, // Callback
    ARGTYPE_CANCEL, // Return 0
    ARGTYPE_SETZERO, // Arg3 becomes 0
    ARGTYPE_INT, // Integer
    ARGTYPE_STRUCT, // Struct Pointer
};

ArgTypes GetArgType(const char* arg_type)
{
    if (arg_type == "CURLOPT_URL")
        return ArgTypes::ARGTYPE_STRING;
    if (arg_type == "CURLOPT_USERAGENT")
        return ArgTypes::ARGTYPE_STRING;
    if (arg_type == "CURLOPT_WRITEDATA")
        return ArgTypes::ARGTYPE_STRUCT;
    if (arg_type == "CURLOPT_USERNAME")
        return ArgTypes::ARGTYPE_STRUCT;
    if (arg_type == "CURLOPT_USERPWD")
        return ArgTypes::ARGTYPE_STRUCT;
    if (arg_type == "CURLOPT_HEADERDATA")
        return ArgTypes::ARGTYPE_AGBINARY;
    if (arg_type == "CURLOPT_READDATA")
        return ArgTypes::ARGTYPE_AGBINARY;
    if (arg_type == "CURLOPT_WRITEFUNCTION")
        return ArgTypes::ARGTYPE_FUNCTION;
    if (arg_type == "CURLOPT_READFUNCTION")
        return ArgTypes::ARGTYPE_FUNCTION;
    if (arg_type == "CURLOPT_SSL_VERIFYPEER")
        return ArgTypes::ARGTYPE_SETZERO;
    if (arg_type == "CURLOPT_SSL_VERIFYHOST")
        return ArgTypes::ARGTYPE_SETZERO;
    if (arg_type == "CURLOPT_INFILESIZE")
        return ArgTypes::ARGTYPE_INT;
    if (arg_type == "CURLOPT_POSTFIELDSIZE")
        return ArgTypes::ARGTYPE_INT;
    if (arg_type == "CURLOPT_CAPATH")
        return ArgTypes::ARGTYPE_STRING;
    if (arg_type == "CURLOPT_CAINFO")
        return ArgTypes::ARGTYPE_STRING;
    if (arg_type == "CURLOPT_HEADERFUNCTION")
        return ArgTypes::ARGTYPE_FUNCTION;
    if (arg_type == "CURLOPT_SSL_CTX_FUNCTION")
        return ArgTypes::ARGTYPE_FUNCTION;
    if (arg_type == "CURLOPT_SSLCERTTYPE")
        return ArgTypes::ARGTYPE_STRING;
    return ArgTypes::ARGTYPE_NONE;
}

// Hooks
namespace OVS::Hooks {
    using namespace Memory::VP;
    using namespace hook;

    bool DisableSignatureCheck(Trampoline* GameTramp)
    {
        wprintf(L"\n==DisableSignatureCheck==\n");
        if (SettingsMgr->pSigCheck.empty())
        {
            printfRed(L"pSigCheck Not Specified. Please Add Pattern to ini file!\n");
            return false;
        }

        PatternFinder lpSigCheckPattern = SettingsMgr->pSigCheck;
        if (!lpSigCheckPattern)
        {
            printfError(L"Couldn't find SigCheck Pattern\n");
            return false;
        }

        lpSigCheckPattern += 0x30;
        if (SettingsMgr->iLogLevel)
            wprintf(L"SigCheck Pattern found at: %p\n", (uint64_t*)lpSigCheckPattern);

        // Old method
        //uint64_t CalledFuncAddr = GetDestinationFromOpCode(hook_address+7, 1, 5, 4);
        //Patch(GetGameAddr(CalledFuncAddr), (uint8_t)0xC3); // ret
        //Patch(GetGameAddr(CalledFuncAddr) + 1, (uint32_t)0x90909090); // Nop

        MakeProxyFromOpCode(
            GameTramp,
            lpSigCheckPattern + 7,
            (uint8_t)4,
            &DummyVoidFunc,
            PATCH_JUMP
        );


        printfSuccess(L"SigCheck Patched");

        return true;
    }

    bool PatchSunsetSetterIntoOVSChecker(Trampoline* GameTramp)
    {
        wprintf(L"\n==Override Sunset Function==\n");
        std::wstring pattern = SettingsMgr->pSunsetDate;
        if (pattern.empty())
        {
            printfError(L"pSunsetDate Not Specified. Please Add Pattern to ini file!");
            return false;
        }

        PatternFinder lpPattern = pattern;
        if (!lpPattern)
        {
            printfError(L"Couldn't find SunsetDate Pattern");
            return false;
        }

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"SunsetDate Pattern Found at: %p\n", (uint64_t*)lpPattern);
        }

        MakeProxyFromOpCode(GameTramp, lpPattern + 7, (uint8_t)4, OVS::Proxies::OVSOfflineModeChecker, &MVSGame::Init_thread_header, PATCH_CALL);
        GetProcFromOpCode(lpPattern + 0x47, 4, &MVSGame::Init_thread_footer);
        GetProcFromOpCode(lpPattern + 0x3B, 4, &MVSGame::FDateTime);
        MVSGame::kSunsetDate = (uint64_t*) GetDestinationFromOpCode(lpPattern + 0x19, 3, 7, 4);
        
        uint64_t final_bool_offset = lpPattern - 0x2F;
        Memory::VP::InjectHook(final_bool_offset, GameTramp->Jump(OVS::Proxies::OVSOfflineModeChecker), PATCH_CALL);
        Patch<uint16_t>(final_bool_offset + 0x5, 0x10EB);

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"Init_thread_header Function Found at: %p\n", MVSGame::Init_thread_header);
            wprintf(L"Init_thread_footer Function Found at: %p\n", MVSGame::Init_thread_footer);
            wprintf(L"FDateTime Function Found at: %p\n", MVSGame::FDateTime);
            wprintf(L"SunsetDate Object Found at: %p\n", MVSGame::kSunsetDate);
        }
                
        // Jump to the cleanup function
        Patch<uint16_t>((uint64_t)lpPattern + 0xC, 0xDAEB);
        
        // Nop the addresses
        uint64_t nop_loc = lpPattern + 0xE;
        for (int i = 0; i < int(0x40 / 9) - 1; i++) // 7 times
        {
            Patch<uint64_t>(nop_loc, 0x841F0F66);
            //Patch<uint8_t>(nop_loc + 8, 0); // Unneeded
            nop_loc += 9;
        }
        for (int i = 0; i < int(12 / 4); i++)
        {
            Patch<uint32_t>(nop_loc, 0x401F0F);
            nop_loc += 4;
        }

        nop_loc = final_bool_offset + 0x7;
        for (int i = 0; i < int(16 / 4); i++)
        {
            Patch<uint32_t>(nop_loc, 0x401F0F);
            nop_loc += 4;
        }

        printfSuccess(L"Sunset Function Proxied");
        return true;
    }

    bool OverrideProdEndpointsData(Trampoline* GameTramp)
    {
        wprintf(L"\n==OverrideGameEndpointsData==\n");
        std::wstring pattern = SettingsMgr->pProdEndpointLoader;
        if (pattern.empty())
        {
            printfError(L"pProdEndpointLoader Not Specified. Please Add Pattern to ini file!");
            return false;
        }

        if (SettingsMgr->szProdServerUrl.empty())
        {
            printfWarning(L"Prod Server Url is empty or not specified. Skipping!");
            return false;
        }

        PatternFinder lpPattern = pattern;
        if (!lpPattern)
        {
            printfError(L"Couldn't find ProdEndpointLoader Pattern");
            return false;
        }

        lpPattern += 0x0A;
        if (SettingsMgr->iLogLevel)
            wprintf(L"ProdEndpointLoader Pattern Found at: %p\n", (uint64_t*)lpPattern);

        MakeProxyFromOpCode(GameTramp, lpPattern, 4, OVS::Proxies::OverrideProdEndpoint, &MVSGame::SetFStringValue, PATCH_CALL);

        printfSuccess(L"ProdEndpointLoader Proxied");
        return true;
    }

    bool OverrideGameEndpointsData(Trampoline* GameTramp)
    {
        wprintf(L"\n==OverrideGameEndpointsData==\n");
        std::wstring pattern = SettingsMgr->pEndpointLoader;
        if (pattern.empty())
        {
            printfError(L"pEndpointLoader Not Specified. Please Add Pattern to ini file!");
            return false;
        }

        if (SettingsMgr->szServerUrl.empty())
        {
            printfWarning(L"Server Url is empty or not specified. Skipping!");
            return false;
        }

        PatternFinder lpPattern = pattern;
        if (!lpPattern)
        {
            printfError(L"Couldn't find EndpointLoader Pattern");
            return false;
        }

        lpPattern += 0x0A;
        if (SettingsMgr->iLogLevel)
            wprintf(L"EndpointLoader Pattern Found at: %p\n", (uint64_t*)lpPattern);

        MakeProxyFromOpCode(GameTramp, lpPattern, 4, OVS::Proxies::OverrideGameEndpoint, &MVSGame::GetEndpointKeyValue, PATCH_CALL);

        printfSuccess(L"EndpointLoader Proxied");
        return true;
    }

    bool DialogHooks(Trampoline* GameTramp)
    {
        wprintf(L"\n==Dialog Funcs==\n");
        if (!HookMetadata::sActiveMods.bUEFuncs)
        {
            printfError(L"UE Funcs were not enabled therefore Dialog cannot be used!");
            return false;
        }

        PatternFinder lpPattern;

        if (SettingsMgr->pDialog.empty())
        {
            printfError(L"pDialog Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        lpPattern = SettingsMgr->pDialog;
        if (!lpPattern)
        {
            printfError(L"Couldn't find Dialog Pattern");
            return false;
        }

        MVSGame::UMvsFrontendManager::AddDialogPtr = (MVSGame::UMvsFrontendManager::AddDialogType)(uint64_t*)(lpPattern + 30); // 0x142766C50
        GetProcFromOpCode(lpPattern + 38, 4, &MVSGame::GetFrontendManager); // 0x142722980

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"Dialog Pattern Found at: %p\n", (uint64_t*)lpPattern);
            wprintf(L"AddDialog at %p\n", MVSGame::UMvsFrontendManager::AddDialogPtr);
            wprintf(L"FrontendManager at %p\n", MVSGame::GetFrontendManager);
        }

        if (!SettingsMgr->pDialogParams.empty())
        {
            lpPattern = SettingsMgr->pDialogParams;
            if (lpPattern)
            {
                MVSGame::FMvsDialogParametersConst = (MVSGame::DialogParamsType*)(uint64_t*)lpPattern; // 0x14276C2F0
                //MVSGame::GLeave_Lobby_Prompt_Text = (MVSGame::FText*)GetGameAddr(0x1480ADDB0);

                if (SettingsMgr->iLogLevel)
                {
                    wprintf(L"DialogParams Pattern Found at: %p\n", (uint64_t*)lpPattern);
                }

            }
        }

        if (SettingsMgr->pDialogCallback.empty())
        {
            printfError(L"pDialogCallback Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        lpPattern = SettingsMgr->pDialogCallback;
        if (!lpPattern)
        {
            printfError(L"Couldn't find DialogCallback Pattern");
            return false;
        }

        GetProcFromOpCode(lpPattern, 4, &MVSGame::SingleParamDialogCallbackSetter); // 0x1428AD240

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"DialogCallback Pattern Found at: %p\n", (uint64_t*)lpPattern);
            wprintf(L"DialogCallback at %p\n", MVSGame::SingleParamDialogCallbackSetter);
        }


        if (SettingsMgr->pQuitGameCallback.empty())
        {
            printfError(L"pQuitGameCallback Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        else
        {
            lpPattern = SettingsMgr->pQuitGameCallback;
            if (!lpPattern)
            {
                printfWarning(L"Couldn't find pQuitGameCallback Pattern. Going with alternative Shutdown mechanism!");
            }
            else
            {
                MVSGame::QuitGame = (MVSGame::QuitGameParamsType*)(uint64_t*)lpPattern; // 0x1428AFCC0

                if (SettingsMgr->iLogLevel)
                {
                    wprintf(L"QuitGameCallback Pattern Found at: %p\n", (uint64_t*)lpPattern);
                    wprintf(L"QuitGameCallback at %p\n", MVSGame::QuitGame);
                }
            }
        }

        printfSuccess(L"Dialog Usable!");
        return true;
    }

    bool NotificationHooks(Trampoline* GameTramp)
    {
        MVSGame::UMvsNotificationManager::GetNotifManagerPtr = (MVSGame::UMvsNotificationManager::GetNotifManagerType)GetGameAddr(0x1428B1D60);

        wprintf(L"\n==Notification Funcs==\n");
        if (!HookMetadata::sActiveMods.bUEFuncs)
        {
            printfError(L"UE Funcs were not enabled therefore Notifications cannot be used!");
            return false;
        }
        
        PatternFinder lpPattern;

        if (SettingsMgr->pNotifs.empty())
        {
            printfError(L"pNotifs Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        lpPattern = SettingsMgr->pNotifs; // This pattern finds 3 functions, but all of them are the exact same so any works
        if (!lpPattern)
        {
            printfError(L"Couldn't find Notifications Pattern");
            return false;
        }

        //MVSGame::UMvsNotificationManager::RequestShowNotificationPtr = (MVSGame::UMvsNotificationManager::RequestShowNotificationType)(uint64_t*)(lpPattern + 32); // 0x142995E40 // 32
        GetProcFromOpCode(lpPattern + 32, 4, &MVSGame::UMvsNotificationManager::RequestShowNotificationPtr);
        //MVSGame::UMvsNotificationManager::GetNotifManagerPtr = (MVSGame::UMvsNotificationManager::GetNotifManagerType)GetGameAddr(0x1428B1D60); // 0x1428B1D60 // 19
        GetProcFromOpCode(lpPattern + 19, 4, &MVSGame::UMvsNotificationManager::GetNotifManagerPtr); // 0x1428B1D60

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"Notifications Pattern Found at: %p\n", (uint64_t*)lpPattern);
            wprintf(L"ShowNotification at %p\n", MVSGame::UMvsNotificationManager::RequestShowNotificationPtr);
            wprintf(L"NotificationManager at %p\n", MVSGame::UMvsNotificationManager::GetNotifManagerPtr);
        }


        printfSuccess(L"Notifications Usable!");
        return true;
    }

    bool HookUEFuncs(Trampoline* GameTramp)
    {
        wprintf(L"\n==UE Funcs==\n");
        if (SettingsMgr->pFText.empty())
        {
            printfError(L"pFText Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        PatternFinder lpPattern = SettingsMgr->pFText;
        if (!lpPattern)
        {
            printfError(L"Couldn't find FText Pattern");
            return false;
        }

        PatternFinder first_address = lpPattern - (uint64_t)26;

        // FText
        GetProcFromOpCode(first_address + 14, 4, &FText::FromString); // 0x142AFE4C0
        GetProcFromOpCode(lpPattern + 3, 4, &FText::FromName); // 0x142AFE350
        GetProcFromOpCode((uint64_t)FText::FromString + 32, 4, &FText::GetEmpty); // 0x142AFE6D0

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"FText Pattern Found at: %p\n", (uint64_t*)lpPattern);
            wprintf(L"FText::FromString at: %p\n", FText::FromString);
            wprintf(L"FText::FromName at: %p\n", FText::FromName);
            wprintf(L"FText::GetEmpty at: %p\n", FText::GetEmpty);
        }

        // FName
        GetProcFromOpCode(first_address, 4, &FName::ToStringPtr);

        if (SettingsMgr->pCFName.empty())
        {
            printfError(L"pCFName Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        lpPattern = SettingsMgr->pCFName;
        if (!lpPattern)
        {
            printfError(L"Couldn't find CFName Pattern");
            return false;
        }

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"CFName Pattern Found at: %p\n", (uint64_t*)lpPattern);
        }

        GetProcFromOpCode(lpPattern + 79, 4, &FName::FNameCharConstructor); // 0x142BB5FC0

        if (SettingsMgr->pWCFname.empty())
        {
            printfError(L"pWCFname Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        lpPattern = SettingsMgr->pWCFname;
        if (!lpPattern)
        {
            printfError(L"Couldn't find WCFname Pattern");
            return false;
        }
        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"WCFname Pattern Found at: %p\n", (uint64_t*)lpPattern);
        }
        FName::FNameWCharConstructor = (FName::FNameConstructorWCharType)GetGameAddr(lpPattern - 32); // 0x142BB6120

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"FName::ToString at: %p\n", FName::ToStringPtr);
            wprintf(L"FName::FName(char*) at: %p\n", FName::FNameCharConstructor);
            wprintf(L"FName::FName(wchar_t*) at: %p\n", FName::FNameWCharConstructor);
        }

        if (SettingsMgr->pFighterInstance.empty())
        {
            printfError(L"pFighterInstance Not Specified. Please Add Pattern to ini file!");
            return false;
        }
        lpPattern = SettingsMgr->pFighterInstance;
        if (!lpPattern)
        {
            printfError(L"Couldn't find FighterInstance Pattern");
            return false;
        }

        uint64_t address = GetDestinationFromOpCode(lpPattern, 3, 7, 4); // Couldn't get inside cuz too small, so get outside's lea, go inside, offset.
        MakeProxyFromOpCode(GameTramp, address + 14, 4, OVS::Proxies::CopyFighterInstance, &MVSGame::UFigherGameInstanceConst, PATCH_JUMP); // 0x1422943BE

        if (SettingsMgr->iLogLevel)
        {
            wprintf(L"FighterInstance Pattern Found at: %p\n", (uint64_t*)lpPattern);
            wprintf(L"FighterInstance Proxied from %p to %p\n", MVSGame::UFigherGameInstanceConst, OVS::Proxies::CopyFighterInstance);
        }

        printfSuccess(L"All Required UE Funcs Found!");

        return true;
    }

};

namespace OVS::Mods {

}

namespace HookMetadata {
    HHOOK KeyboardProcHook = nullptr;
    HMODULE CurrentDllModule = NULL;
    HANDLE Console = NULL;

};