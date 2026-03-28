#pragma once
#include<string>
#include "..\IniReader.h"

class eSettingsManager {
public:
    void Init();

public:
    // Settings

    bool bEnableKeyboardHotkeys;

    // Debug
    bool bEnableConsoleWindow;
    bool bPauseOnStart;
    int	iLogLevel;
    bool bDebug;
    bool bAllowNonMVS;
    bool bLoadDumper;
    bool bRunDumperOnLoad;
    std::string sDumperDLLPath;


    // Toggles
    bool bSunsetDate;
    bool bDisableSignatureCheck;
    bool bHookUE;
    bool bDialog;
    bool bNotifs;

    // Addresses

    // Patterns
    std::string pSigCheck;
    std::string pEndpointLoader;
    std::string pProdEndpointLoader;
    std::string pSunsetDate;
    std::string pFText;
    std::string pCFName;
    std::string pWCFname;
    std::string pDialog;
    std::string pDialogParams;
    std::string pFighterInstance;
    std::string pDialogCallback;
    std::string pQuitGameCallback;
    std::string	pNotifs;


    // Menu Section
    std::string hkMenu;
    int iVKMenuToggle;

    //Other
    int iLogSize;
    bool FORCE_CHECK_VER = false;
    std::string szGameVer;
    std::string szModLoader;
    std::string szAntiCheatEngine;
    std::string szCurlSetOpt;
    std::string szCurlPerform;

    //Private Server
    std::string szServerUrl;
    bool bEnableServerProxy;
    // WB
    std::string szProdServerUrl;
    bool bEnableProdServerProxy;

};

class eFirstRunManager
{
public:
    void Init();
    void Save();

public:
    bool bPaidModWarned = false;

private:
    CIniReader* ini;
};

class eCachedPatternsManager
{
private:
    char* Hash = nullptr;
    CIniReader* ini;
    static __int64 GameAddr;

public:
    void Init(uint64_t Hash, const char* version);
    void Save(char *key, uint64_t offset);
    uint64_t eCachedPatternsManager::Load(char* key);
    
    ~eCachedPatternsManager() { if (Hash) delete[] Hash; }
};

extern eSettingsManager*		SettingsMgr;
extern eFirstRunManager*		FirstRunMgr;
extern eCachedPatternsManager*	CachedPatternsMgr;