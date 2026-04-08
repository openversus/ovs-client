#pragma once
#include <string>
#include <map>

namespace OVS
{
    enum LogLevel
    {
        LOG_DEBUG,
        LOG_INFO,
        LOG_WARN,
        LOG_ERROR,
        LOG_CRITICAL
    };

    extern const wchar_t* WDEBUG_PREFIX;
    extern const wchar_t* WINFO_PREFIX;
    extern const wchar_t* WWARN_PREFIX;
    extern const wchar_t* WERROR_PREFIX;
    extern const wchar_t* WCRITICAL_PREFIX;

    extern const wchar_t* INJECTOR_NAME;
    extern const wchar_t* INJECTOR_VERSION;
    extern const wchar_t* INJECTOR_DESCRIPTION;

    extern std::map<LogLevel, std::wstring> LogPrefixMap;
    extern std::map<LogLevel, std::wostream*> LogStreams;

    extern uint64_t EXEHash;
    inline constexpr const wchar_t* OVS_Name = L"OpenVersus";
    inline constexpr const wchar_t* OVS_Config_Ext = L".ini";
    inline const std::wstring OVS_Config_File = std::wstring(OVS_Name) + std::wstring(OVS_Config_Ext);
    inline constexpr const wchar_t* OVS_Version = L"2026.04.08.05";
    inline constexpr const wchar_t* ServerUrl = L"http://testing.openversus.org:8000/";
    inline constexpr const wchar_t* ProdServerUrl = ServerUrl;

    inline const std::wstring& GetCurrentVersion()
    {
        static const std::wstring version(OVS::OVS_Version);
        return version;
    }

    // OVSSetting struct definition
    struct OVSSetting
    {
        const wchar_t* Name = L"";
        const wchar_t* Section = L"";
        const wchar_t* Setting = L"";
        const wchar_t* Type = L"string";
        const wchar_t* ValueString = L"";
        size_t ValueInt = 0;
        bool ValueBool = false;
        void* ValuePtr = nullptr;

        OVSSetting(
            const wchar_t* Name,
            const wchar_t* Section,
            const wchar_t* Setting,
            const wchar_t* Type,
            const wchar_t* ValueString
        )
        {
            this->Name = Name;
            this->Section = Section;
            this->Setting = Setting;
            this->Type = Type;
            this->ValueString = ValueString;
        }

        OVSSetting(
            const wchar_t* Name,
            const wchar_t* Section,
            const wchar_t* Setting,
            const wchar_t* Type,
            size_t ValueInt,
            bool isInt = true
        )
        {
            this->Name = Name;
            this->Section = Section;
            this->Setting = Setting;
            this->Type = Type;
            this->ValueInt = ValueInt;
        }

        OVSSetting(
            const wchar_t* Name,
            const wchar_t* Section,
            const wchar_t* Setting,
            const wchar_t* Type,
            bool ValueBool
        )
        {
            this->Name = Name;
            this->Section = Section;
            this->Setting = Setting;
            this->Type = Type;
            this->ValueBool = ValueBool;
        }

        template<typename T>
        T Value()
        {
            if constexpr (std::is_same_v<T, const wchar_t*>)
            {
                return this->ValueString;
            }
            else if constexpr (std::is_same_v<T, size_t> || std::is_same_v<T, int>)
            {
                return this->ValueInt;
            }
            else if constexpr (std::is_same_v<T, bool>)
            {
                return this->ValueBool;
            }
            else if constexpr (std::is_same_v<T, void*>)
            {
                return this->ValuePtr;
            }
            return T{};
        }
    };

    // Forward declarations
    OVSSetting GetOVSSettingByName(const wchar_t* name);

    struct OVSDefaultSettings
    {
        static inline OVSSetting bEnableConsoleWindow = GetOVSSettingByName(L"bEnableConsoleWindow");
        static inline OVSSetting bPauseOnStart = GetOVSSettingByName(L"bPauseOnStart");
        static inline OVSSetting bDebug = GetOVSSettingByName(L"bDebug");
        static inline OVSSetting bAllowNonMVS = GetOVSSettingByName(L"bAllowNonMVS");
        static inline OVSSetting iLogSize = GetOVSSettingByName(L"iLogSize");
        static inline OVSSetting iLogLevel = GetOVSSettingByName(L"iLogLevel");
        static inline OVSSetting szModLoader = GetOVSSettingByName(L"szModLoader");
        static inline OVSSetting szAntiCheatEngine = GetOVSSettingByName(L"szAntiCheatEngine");
        static inline OVSSetting szCurlSetOpt = GetOVSSettingByName(L"szCurlSetOpt");
        static inline OVSSetting szCurlPerform = GetOVSSettingByName(L"szCurlPerform");
        static inline OVSSetting bEnableKeyboardHotkeys = GetOVSSettingByName(L"bEnableKeyboardHotkeys");
        static inline OVSSetting hkMenu = GetOVSSettingByName(L"hkMenu");
        static inline OVSSetting bSunsetDate = GetOVSSettingByName(L"bSunsetDate");
        static inline OVSSetting bDisableSignatureCheck = GetOVSSettingByName(L"bDisableSignatureCheck");
        static inline OVSSetting bHookUE = GetOVSSettingByName(L"bHookUE");
        static inline OVSSetting bDialog = GetOVSSettingByName(L"bDialog");
        static inline OVSSetting bNotifs = GetOVSSettingByName(L"bNotifs");
        static inline OVSSetting pSigCheck = GetOVSSettingByName(L"pSigCheck");
        static inline OVSSetting pEndpointLoader = GetOVSSettingByName(L"pEndpointLoader");
        static inline OVSSetting pProdEndpointLoader = GetOVSSettingByName(L"pProdEndpointLoader");
        static inline OVSSetting pSunsetDate = GetOVSSettingByName(L"pSunsetDate");
        static inline OVSSetting pFText = GetOVSSettingByName(L"pFText");
        static inline OVSSetting pCFName = GetOVSSettingByName(L"pCFName");
        static inline OVSSetting pWCFname = GetOVSSettingByName(L"pWCFname");
        static inline OVSSetting pDialog = GetOVSSettingByName(L"pDialog");
        static inline OVSSetting pDialogParams = GetOVSSettingByName(L"pDialogParams");
        static inline OVSSetting pDialogCallback = GetOVSSettingByName(L"pDialogCallback");
        static inline OVSSetting pQuitGameCallback = GetOVSSettingByName(L"pQuitGameCallback");
        static inline OVSSetting pFighterInstance = GetOVSSettingByName(L"pFighterInstance");
        static inline OVSSetting pNotifs = GetOVSSettingByName(L"pNotifs");
        static inline OVSSetting szServerUrl = GetOVSSettingByName(L"szServerUrl");
        static inline OVSSetting szProdServerUrl = GetOVSSettingByName(L"szProdServerUrl");
        static inline OVSSetting bEnableServerProxy = GetOVSSettingByName(L"bEnableServerProxy");
        static inline OVSSetting bEnableProdServerProxy = GetOVSSettingByName(L"bEnableProdServerProxy");
    };

    namespace Patterns
    {
        // Patterns valid for the final patch of the game, Unreal Engine version 5.1.1.0

        // General
        inline constexpr const wchar_t* SigCheck = L"48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC 48 83 EC 28 E8 ? ? ? ? 48 89 05 ? ? ? ? 48 83 C4 28 C3 CC CC CC CC CC CC CC CC CC CC CC 48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC 48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC";
        inline constexpr const wchar_t* EndpointLoader = L"48 8b cb 48 8d 15 ? ? ? ? E8 ? ? ? ? 48 8b 4c 24 ? 48 85 c9 74 05 E8 ? ? ? ? 48 8b c3 48 8b 5c 24";
        inline constexpr const wchar_t* ProdEndpointLoader = L"49 ? ? 48 8d 15 ? ? ? ? E8 ? ? ? ? 49 ? ? ? ? 00 00 48 8b";
        inline constexpr const wchar_t* SunsetDate = L"48 8d 0d ? ? ? ? e8 ? ? ? ? 83 3D ? ? ? ? FF 75 ? 89 7C";

        // Unreal Engine
        inline constexpr const wchar_t* FText = L"4C 8B ? E8 ? ? ? ? 4C 8D ? ? ? ? ? 4C 8B ? 4C 8D ? ? ? ? ? 48 8D ? ? ? ? ? 48 8D ? ? E8 ? ? ? ? 48 8B ? 48 8D ? 00 E8 ? ? ? ? 49 8B ? 00 4C 8B ? 48 89 ? ? ? 00 00 49 8B ? ? C7 85 ? ? ? ? 04 00 00 00";
        inline constexpr const wchar_t* CFName = L"4C 8D 9C ? ? ? ? ? 49 8B ? ? 49 8B ? ? 4D 8B ? ? 4D 8B ? ? 41 ? ? ? ? 49 8B ? ? C3 48 8D ? ? ? ? ? E8 ? ? ? ? 83 3D ? ? ? ? FF 0F 85 ? ? ? ? 41 B8 01 00 00 00 48 8D ? ? ? ? ? 48 8D ? ? ? ? ? E8 ? ? ? ? 48 8D ? ? ? ? ? E8 ? ? ? ? E9 ? ? ? ? CC CC CC CC CC CC CC CC";
        inline constexpr const wchar_t* WCFName = L"48 85 ? 74 1E 0F ? ? 66 85 C0 74 16 0F ? ?";

        // MVS-specific
        inline constexpr const wchar_t* Dialog = L"40 ? 48 83 ? ? 48 ? ? E8 ? ? ? ? 48 85 ? 75 ? 48 8B ? 48";
        inline constexpr const wchar_t* DialogParams = L"48 89 ? ? ? 48 89 ? ? ? 48 89 ? ? ? 48 89 ? ? ? 41 ? 48 83 ? ? 41 0F ? ? 48 ? ? 48 ? ? E8 ? ? ? ? 48";
        inline constexpr const wchar_t* DialogCallback = L"E8 ? ? ? ? 48 8D 15 ? ? ? ? 48 8D 4C ? ? E8 ? ? ? ? 4C ? ? ? ? C6 ? ? ? 00 48 8D ? ? ? 49 8B CE";
        inline constexpr const wchar_t* QuitGameCallback = L"40 ? 48 83 ? ? 48 8B ? 48 83 ? ? E8 ? ? ? ? 84 C0 74 19 48 8B ? ? 45 33 C9 45 33 C0";
        inline constexpr const wchar_t* FighterInstance = L"48 8D 05 ? ? ? 00 49 89 ? ? 48 8D ? ? ? ? ? 49 89 ? ? 49 C7 ? ? 00 00 00 00 C7 44 ? ? 08 00 00 10 C7 44 ? ? 08 00 00 00 C7 44 ? ? 70 08 00 00";
        inline constexpr const wchar_t* Notifications = L"FF 50 ? F3 0F 10 ? ? ? ? ? 48 8B ? F3 0F ? ? ? E8";
    }
}
