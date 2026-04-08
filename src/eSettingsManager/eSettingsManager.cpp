#include "constants.hpp"
#include "utils/prettyprint.h"
#include "ovs/OpenVersus.h"
#include "eSettingsManager/eSettingsManager.h"
#include <Windows.h>

eSettingsManager*		SettingsMgr			= new eSettingsManager();
eFirstRunManager*		FirstRunMgr			= new eFirstRunManager;
eCachedPatternsManager*	CachedPatternsMgr	= new eCachedPatternsManager;
__int64 eCachedPatternsManager::GameAddr = reinterpret_cast<__int64>(GetModuleHandle(nullptr));

void eCachedPatternsManager::Init(uint64_t Hash, const wchar_t* version)
{
    ini = new CIniReader(L"PatternsCache.cache");
    if (Hash)
    {
        size_t totalLen = 8 + 1 + wcslen(version) + 1; // Hex, ., ver, \0
        wchar_t* hashStr = new wchar_t[totalLen];
        swprintf_s(hashStr, totalLen, L"%08X.%s", (uint32_t)(Hash >> 32), version);
        eCachedPatternsManager::Hash = hashStr;
    }
    else
        eCachedPatternsManager::Hash = nullptr;

}

void eCachedPatternsManager::Save(wchar_t* key, uint64_t offset)
{
    if (Hash)
    {
        if (offset > static_cast<uint64_t>(GameAddr))
        {
            offset -= GameAddr;
        }
        ini->WriteInteger(Hash, key, offset);
    }
}

uint64_t eCachedPatternsManager::Load(wchar_t* key)
{
    if (Hash)
    {
        uint64_t result = ini->ReadInteger(Hash, key, 0);
        if (result)
            return result + GameAddr;
    }
    return 0;
}

void eFirstRunManager::Save()
{
    ini->WriteBoolean(L"FirstRun", L"PaidModWarned", true);
}

void eSettingsManager::Init()
{
    CIniReader ini(L"");

    OVS::OVSSetting* DefaultSettings = OVS::OVSDefaultSettingsArray;
    int numSettings = static_cast<int>(OVS::OVSDefaultSettingsArrayCount);
    OVS::OVSDefaultSettings defaults;

    bEnableConsoleWindow = ini.ReadBoolean(defaults.bEnableConsoleWindow.Section, defaults.bEnableConsoleWindow.Setting, defaults.bEnableConsoleWindow.Value<bool>());
    Save(&ini, OVS::OVSSetting {
        L"bEnableConsoleWindow",
        defaults.bEnableConsoleWindow.Section,
        defaults.bEnableConsoleWindow.Setting,
        defaults.bEnableConsoleWindow.Type,
        bEnableConsoleWindow
    });
    bPauseOnStart = ini.ReadBoolean(defaults.bPauseOnStart.Section, defaults.bPauseOnStart.Setting, defaults.bPauseOnStart.Value<bool>());
    Save(&ini, OVS::OVSSetting {
        L"bPauseOnStart",
        defaults.bPauseOnStart.Section,
        defaults.bPauseOnStart.Setting,
        defaults.bPauseOnStart.Type,
        bPauseOnStart
    });

    bDebug = ini.ReadBoolean(defaults.bDebug.Section, defaults.bDebug.Setting, defaults.bDebug.Value<bool>());
    Save(&ini, OVS::OVSSetting {
        L"bDebug",
        defaults.bDebug.Section,
        defaults.bDebug.Setting,
        defaults.bDebug.Type,
        bDebug
    });

    bAllowNonMVS = ini.ReadBoolean(defaults.bAllowNonMVS.Section, defaults.bAllowNonMVS.Setting, defaults.bAllowNonMVS.Value<bool>());
    Save(&ini, OVS::OVSSetting {
        L"bAllowNonMVS",
        defaults.bAllowNonMVS.Section,
        defaults.bAllowNonMVS.Setting,
        defaults.bAllowNonMVS.Type,
        bAllowNonMVS
    });
    
    iLogSize = ini.ReadInteger(defaults.iLogSize.Section, defaults.iLogSize.Setting, static_cast<size_t>(defaults.iLogSize.Value<size_t>()));
    Save(&ini, OVS::OVSSetting {
        L"iLogSize",
        defaults.iLogSize.Section,
        defaults.iLogSize.Setting,
        defaults.iLogSize.Type,
        iLogSize
    });
    
    iLogLevel = ini.ReadInteger(defaults.iLogLevel.Section, defaults.iLogLevel.Setting, static_cast<size_t>(defaults.iLogLevel.Value<size_t>()));
    Save(&ini, OVS::OVSSetting {
        L"iLogLevel",
        defaults.iLogLevel.Section,
        defaults.iLogLevel.Setting,
        defaults.iLogLevel.Type,
        iLogLevel
    });
    
    szModLoader = ini.ReadString(defaults.szModLoader.Section, defaults.szModLoader.Setting, defaults.szModLoader.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting {
        L"szModLoader",
        defaults.szModLoader.Section,
        defaults.szModLoader.Setting,
        defaults.szModLoader.Type,
        szModLoader.c_str()
    });
    
    szAntiCheatEngine = ini.ReadString(defaults.szAntiCheatEngine.Section, defaults.szAntiCheatEngine.Setting, defaults.szAntiCheatEngine.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting {
        L"szAntiCheatEngine",
        defaults.szAntiCheatEngine.Section,
        defaults.szAntiCheatEngine.Setting,
        defaults.szAntiCheatEngine.Type,
        szAntiCheatEngine.c_str()
    });
    
    szCurlSetOpt = ini.ReadString(defaults.szCurlSetOpt.Section, defaults.szCurlSetOpt.Setting, defaults.szCurlSetOpt.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"szCurlSetOpt",
        defaults.szCurlSetOpt.Section,
        defaults.szCurlSetOpt.Setting,
        defaults.szCurlSetOpt.Type,
        szCurlSetOpt.c_str()
    });
    
    szCurlPerform = ini.ReadString(defaults.szCurlPerform.Section, defaults.szCurlPerform.Setting, defaults.szCurlPerform.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"szCurlPerform",
        defaults.szCurlPerform.Section,
        defaults.szCurlPerform.Setting,
        defaults.szCurlPerform.Type,
        szCurlPerform.c_str()
    });
    
    bEnableKeyboardHotkeys = ini.ReadBoolean(defaults.bEnableKeyboardHotkeys.Section, defaults.bEnableKeyboardHotkeys.Setting, defaults.bEnableKeyboardHotkeys.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bEnableKeyboardHotkeys",
        defaults.bEnableKeyboardHotkeys.Section,
        defaults.bEnableKeyboardHotkeys.Setting,
        defaults.bEnableKeyboardHotkeys.Type,
        bEnableKeyboardHotkeys
    });

    hkMenu = ini.ReadString(defaults.hkMenu.Section, defaults.hkMenu.Setting, defaults.hkMenu.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"hkMenu",
        defaults.hkMenu.Section,
        defaults.hkMenu.Setting,
        defaults.hkMenu.Type,
        hkMenu.c_str()
    });

    bSunsetDate = ini.ReadBoolean(defaults.bSunsetDate.Section, defaults.bSunsetDate.Setting, defaults.bSunsetDate.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bSunsetDate",
        defaults.bSunsetDate.Section,
        defaults.bSunsetDate.Setting,
        defaults.bSunsetDate.Type,
        bSunsetDate
    });

    bDisableSignatureCheck = ini.ReadBoolean(defaults.bDisableSignatureCheck.Section, defaults.bDisableSignatureCheck.Setting, defaults.bDisableSignatureCheck.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bDisableSignatureCheck",
        defaults.bDisableSignatureCheck.Section,
        defaults.bDisableSignatureCheck.Setting,
        defaults.bDisableSignatureCheck.Type,
        bDisableSignatureCheck
    });

    bHookUE = ini.ReadBoolean(defaults.bHookUE.Section, defaults.bHookUE.Setting, defaults.bHookUE.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bHookUE",
        defaults.bHookUE.Section,
        defaults.bHookUE.Setting,
        defaults.bHookUE.Type,
        bHookUE
    });

    bDialog = ini.ReadBoolean(defaults.bDialog.Section, defaults.bDialog.Setting, defaults.bDialog.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bDialog",
        defaults.bDialog.Section,
        defaults.bDialog.Setting,
        defaults.bDialog.Type,
        bDialog
    });

    bNotifs = ini.ReadBoolean(defaults.bNotifs.Section, defaults.bNotifs.Setting, defaults.bNotifs.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bNotifs",
        defaults.bNotifs.Section,
        defaults.bNotifs.Setting,
        defaults.bNotifs.Type,
        bNotifs
    });

    pSigCheck = ini.ReadString(defaults.pSigCheck.Section, defaults.pSigCheck.Setting, defaults.pSigCheck.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pSigCheck",
        defaults.pSigCheck.Section,
        defaults.pSigCheck.Setting,
        defaults.pSigCheck.Type,
        pSigCheck.c_str()
    });

    pEndpointLoader = ini.ReadString(defaults.pEndpointLoader.Section, defaults.pEndpointLoader.Setting, defaults.pEndpointLoader.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pEndpointLoader",
        defaults.pEndpointLoader.Section,
        defaults.pEndpointLoader.Setting,
        defaults.pEndpointLoader.Type,
        pEndpointLoader.c_str()
    });

    pProdEndpointLoader = ini.ReadString(defaults.pProdEndpointLoader.Section, defaults.pProdEndpointLoader.Setting, defaults.pProdEndpointLoader.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pProdEndpointLoader",
        defaults.pProdEndpointLoader.Section,
        defaults.pProdEndpointLoader.Setting,
        defaults.pProdEndpointLoader.Type,
        pProdEndpointLoader.c_str()
    });

    pSunsetDate = ini.ReadString(defaults.pSunsetDate.Section, defaults.pSunsetDate.Setting, defaults.pSunsetDate.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pSunsetDate",
        defaults.pSunsetDate.Section,
        defaults.pSunsetDate.Setting,
        defaults.pSunsetDate.Type,
        pSunsetDate.c_str()
    });

    pFText = ini.ReadString(defaults.pFText.Section, defaults.pFText.Setting, defaults.pFText.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pFText",
        defaults.pFText.Section,
        defaults.pFText.Setting,
        defaults.pFText.Type,
        pFText.c_str()
    });

    pCFName = ini.ReadString(defaults.pCFName.Section, defaults.pCFName.Setting, defaults.pCFName.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pCFName",
        defaults.pCFName.Section,
        defaults.pCFName.Setting,
        defaults.pCFName.Type,
        pCFName.c_str()
    });

    pWCFname = ini.ReadString(defaults.pWCFname.Section, defaults.pWCFname.Setting, defaults.pWCFname.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pWCFname",
        defaults.pWCFname.Section,
        defaults.pWCFname.Setting,
        defaults.pWCFname.Type,
        pWCFname.c_str()
    });

    pDialog = ini.ReadString(defaults.pDialog.Section, defaults.pDialog.Setting, defaults.pDialog.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pDialog",
        defaults.pDialog.Section,
        defaults.pDialog.Setting,
        defaults.pDialog.Type,
        pDialog.c_str()
    });

    pDialogParams = ini.ReadString(defaults.pDialogParams.Section, defaults.pDialogParams.Setting, defaults.pDialogParams.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pDialogParams",
        defaults.pDialogParams.Section,
        defaults.pDialogParams.Setting,
        defaults.pDialogParams.Type,
        pDialogParams.c_str()
    });

    pDialogCallback = ini.ReadString(defaults.pDialogCallback.Section, defaults.pDialogCallback.Setting, defaults.pDialogCallback.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pDialogCallback",
        defaults.pDialogCallback.Section,
        defaults.pDialogCallback.Setting,
        defaults.pDialogCallback.Type,
        pDialogCallback.c_str()
    });

    pQuitGameCallback = ini.ReadString(defaults.pQuitGameCallback.Section, defaults.pQuitGameCallback.Setting, defaults.pQuitGameCallback.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pQuitGameCallback",
        defaults.pQuitGameCallback.Section,
        defaults.pQuitGameCallback.Setting,
        defaults.pQuitGameCallback.Type,
        pQuitGameCallback.c_str()
    });

    pFighterInstance = ini.ReadString(defaults.pFighterInstance.Section, defaults.pFighterInstance.Setting, defaults.pFighterInstance.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pFighterInstance",
        defaults.pFighterInstance.Section,
        defaults.pFighterInstance.Setting,
        defaults.pFighterInstance.Type,
        pFighterInstance.c_str()
    });

    pNotifs = ini.ReadString(defaults.pNotifs.Section, defaults.pNotifs.Setting, defaults.pNotifs.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"pNotifs",
        defaults.pNotifs.Section,
        defaults.pNotifs.Setting,
        defaults.pNotifs.Type,
        pNotifs.c_str()
    });

    szServerUrl = ini.ReadString(defaults.szServerUrl.Section, defaults.szServerUrl.Setting, defaults.szServerUrl.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"szServerUrl",
        defaults.szServerUrl.Section,
        defaults.szServerUrl.Setting,
        defaults.szServerUrl.Type,
        szServerUrl.c_str()
    });

    szProdServerUrl = ini.ReadString(defaults.szProdServerUrl.Section, defaults.szProdServerUrl.Setting, defaults.szProdServerUrl.Value<const wchar_t*>());
    Save(&ini, OVS::OVSSetting{
        L"szProdServerUrl",
        defaults.szProdServerUrl.Section,
        defaults.szProdServerUrl.Setting,
        defaults.szProdServerUrl.Type,
        szProdServerUrl.c_str()
    });

    bEnableServerProxy = ini.ReadBoolean(defaults.bEnableServerProxy.Section, defaults.bEnableServerProxy.Setting, defaults.bEnableServerProxy.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bEnableServerProxy",
        defaults.bEnableServerProxy.Section,
        defaults.bEnableServerProxy.Setting,
        defaults.bEnableServerProxy.Type,
        bEnableServerProxy
    });

    bEnableProdServerProxy = ini.ReadBoolean(defaults.bEnableProdServerProxy.Section, defaults.bEnableProdServerProxy.Setting, defaults.bEnableProdServerProxy.Value<bool>());
    Save(&ini, OVS::OVSSetting{
        L"bEnableProdServerProxy",
        defaults.bEnableProdServerProxy.Section,
        defaults.bEnableProdServerProxy.Setting,
        defaults.bEnableProdServerProxy.Type,
        bEnableProdServerProxy
    });

    ini.GetConfigFilename();
}

bool eSettingsManager::Save(CIniReader* ini, OVS::OVSSetting value)
{
    switch (value.Type[0])
    {
        case 'b': // bool
            try
            {
                ini->WriteBoolean(value.Section, value.Setting, value.Value<bool>());
                return true;
            }
            catch (...)
            {
                return false;
            }
            break;
        case 'i': // int
            try
            {
                ini->WriteInteger(value.Section, value.Setting, value.Value<size_t>());
                return true;
            }
            catch (...)
            {
                return false;
            }
            break;
        case 's': // string
            try
            {
                ini->WriteString(value.Section, value.Setting, value.Value<const wchar_t*>());
                return true;

            }
            catch (...)
            {
                return false;
            }
            break;
        default:
            return false;
    }

    return false;
}

bool SaveSettings(CIniReader* ini, OVS::OVSSetting values[])
{
    bool shouldReturnFalse = false;
    std::map<std::wstring, bool> saveResults;
    int numSettings = sizeof(values) / sizeof(values[0]);

    for (int i = 0; i < numSettings; i++)
    {
        saveResults[values[i].Name] = SettingsMgr->Save(ini, values[i]);
    }

    for (int i = 1; i < numSettings; i++)
    {
        if (!saveResults[values[i].Name])
        {
            printfWarning(L"Failed to save setting: %ls (%ls)", values[i].Setting, values[i].Name);
            shouldReturnFalse = true;
        }
    }

    if (shouldReturnFalse)
    {
        printfError(L"One or more settings failed to save. Please check the warnings above.");
        return false;
    }

    return true;

}
