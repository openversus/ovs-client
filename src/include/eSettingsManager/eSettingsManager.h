#pragma once
#include "constants.hpp"
#include <string>
#include "IniReader/IniReader.h"

class eSettingsManager {
public:
    void Init();

public:
    // Settings

    bool bEnableKeyboardHotkeys;

    // Debug
    bool   bEnableConsoleWindow;
    bool   bPauseOnStart;
    size_t iLogLevel;
    bool   bDebug;
    bool   bAllowNonMVS;


    // Toggles
    bool bSunsetDate;
    bool bDisableSignatureCheck;
    bool bHookUE;
    bool bDialog;
    bool bNotifs;

    // Addresses

    // Patterns
    std::wstring pSigCheck;
    std::wstring pEndpointLoader;
    std::wstring pProdEndpointLoader;
    std::wstring pSunsetDate;
    std::wstring pFText;
    std::wstring pCFName;
    std::wstring pWCFname;
    std::wstring pDialog;
    std::wstring pDialogParams;
    std::wstring pFighterInstance;
    std::wstring pDialogCallback;
    std::wstring pQuitGameCallback;
    std::wstring pNotifs;


    // Menu Section
    std::wstring hkMenu;
    size_t iVKMenuToggle;

    //Other
    size_t iLogSize;
    bool FORCE_CHECK_VER = false;
    std::wstring szGameVer;
    std::wstring szModLoader;
    std::wstring szAntiCheatEngine;
    std::wstring szCurlSetOpt;
    std::wstring szCurlPerform;

    //OVS
    bool bAutoUpdate = true;

    //Private Server
    std::wstring szServerUrl;
    bool bEnableServerProxy;
    // WB
    std::wstring szProdServerUrl;
    bool bEnableProdServerProxy;


    eSettingsManager()
    {
        // Set default values for settings
        bEnableKeyboardHotkeys = true;
        bAutoUpdate = true;
        bEnableConsoleWindow = true;
        bPauseOnStart = false;
        iLogLevel = 0;
        bDebug = false;
        bAllowNonMVS = false;
        bSunsetDate = true;
        bDisableSignatureCheck = true;
        bHookUE = true;
        bDialog = true;
        bNotifs = true;
        hkMenu = L"F1";
        iVKMenuToggle = 0x70; // VK_F1
        iLogSize = 50;
        bEnableServerProxy = true;
        bEnableProdServerProxy = true;

        pSigCheck = L"";
        pEndpointLoader = L"";
        pProdEndpointLoader = L"";
        pSunsetDate = L"";
        pFText = L"";
        pCFName = L"";
        pWCFname = L"";
        pDialog = L"";
        pDialogParams = L"";
        pFighterInstance = L"";
        pDialogCallback = L"";
        pQuitGameCallback = L"";
        pNotifs = L"";

        szGameVer = L"";
        szModLoader = L"";
        szAntiCheatEngine = L"";
        szCurlSetOpt = L"";
        szCurlPerform = L"";

        szServerUrl = L"";
        szProdServerUrl = L"";
    }

    bool Save(CIniReader* ini, OVS::OVSSetting value);
    bool SaveSettings(CIniReader* ini, OVS::OVSSetting values[]);

    private:
        CIniReader* ini;
};

class eFirstRunManager
{
public:
    void Save();

public:
    bool bPaidModWarned = false;

private:
    CIniReader* ini;

public:
    eFirstRunManager() {
        ini = nullptr;
    }
    ~eFirstRunManager()
    {
        if (ini)
        {
            delete ini;
        }
    }

    void Init()
    {
        if (nullptr == ini)
        {
            ini = new CIniReader(L"OVSState.ini");
        }
        bPaidModWarned = ini->ReadBoolean(L"FirstRun", L"PaidModWarned", false);
    }
};

class eCachedPatternsManager
{
private:
    wchar_t* Hash = nullptr;
    CIniReader* ini = nullptr;
    static __int64 GameAddr;

public:
    void Init(uint64_t Hash, const wchar_t* version);
    void Save(wchar_t *key, uint64_t offset);
    uint64_t eCachedPatternsManager::Load(wchar_t* key);
    
    ~eCachedPatternsManager() { if (Hash) delete[] Hash; }
};

extern eSettingsManager*		SettingsMgr;
extern eFirstRunManager*		FirstRunMgr;
extern eCachedPatternsManager*	CachedPatternsMgr;
