#include "eSettingsManager.h"
#include <Windows.h>

eSettingsManager*		SettingsMgr			= new eSettingsManager();
eFirstRunManager*		FirstRunMgr			= new eFirstRunManager;
eCachedPatternsManager*	CachedPatternsMgr	= new eCachedPatternsManager;
__int64 eCachedPatternsManager::GameAddr = reinterpret_cast<__int64>(GetModuleHandle(nullptr));

void eCachedPatternsManager::Init(uint64_t Hash, const char* version)
{
    ini = new CIniReader("PatternsCache.cache");
    if (Hash)
    {
        size_t totalLen = 8 + 1 + strlen(version) + 1; // Hex, ., ver, \0
        char* hashStr = new char[totalLen];
        sprintf_s(hashStr, totalLen, "%08X.%s", (uint32_t)(Hash >> 32), version);
        eCachedPatternsManager::Hash = hashStr;
    }
    else
        eCachedPatternsManager::Hash = nullptr;

}

void eCachedPatternsManager::Save(char* key, uint64_t offset)
{
    if (Hash)
    {
        if (offset > GameAddr)
        {
            offset -= GameAddr;
        }
        ini->WriteInteger(Hash, key, offset);
    }
}

uint64_t eCachedPatternsManager::Load(char* key)
{
    if (Hash)
    {
        uint64_t result = ini->ReadInteger(Hash, key, 0);
        if (result)
            return result + GameAddr;
    }
    return 0;
}

void eFirstRunManager::Init()
{
    ini = new CIniReader("OVSState.ini");
    bPaidModWarned = ini->ReadBoolean("FirstRun", "PaidModWarned", false);
}

void eFirstRunManager::Save()
{
    ini->WriteBoolean("FirstRun", "PaidModWarned", true);
}

void eSettingsManager::Init()
{
    CIniReader ini("");

    // Debug Settings
    bEnableConsoleWindow		= ini.ReadBoolean	("Settings.Debug",		"ShowConsole",				false);
    bPauseOnStart				= ini.ReadBoolean	("Settings.Debug",		"DebugPause",				false);
    bDebug						= ini.ReadBoolean	("Settings.Debug",		"DebugLogging",				false);
    bAllowNonMVS				= ini.ReadBoolean	("Settings.Debug",		"NonMVSPatching",			false);
    bLoadDumper                 = ini.ReadBoolean   ("Settings.Debug",      "LoadDumper",               false);
    bRunDumperOnLoad            = ini.ReadBoolean   ("Settings.Debug",      "RunDumperOnLoad",          false);
    sDumperDLLPath              = ini.ReadString    ("Settings.Debug",      "DumperDLLPath",               "");
    
    // Settings
    iLogSize					= ini.ReadInteger	("Settings",			"LogSize",					50);
    iLogLevel					= ini.ReadInteger	("Settings",			"LogLevel",					0);
    szModLoader					= ini.ReadString	("Settings",			"ModLoader",				"Kernel32.CreateFileW");
    szAntiCheatEngine			= ini.ReadString	("Settings",			"AntiCheatEngine",			"User32.EnumChildWindows");
    szCurlSetOpt				= ini.ReadString	("Settings",			"CurlSetOpt",				"libcurl.curl_easy_setopt");
    szCurlPerform				= ini.ReadString	("Settings",			"CurlPerform",				"libcurl.curl_easy_perform");
    bEnableKeyboardHotkeys		= ini.ReadBoolean	("Settings",			"EnableKeyboardHotkeys",	true);
    // Keybinds
    hkMenu						= ini.ReadString	("Settings.Keybinds",	"ToggleMenu",				"F1");

    // Patches
    bSunsetDate					= ini.ReadBoolean   ("Patches",             "SunsetDate",				true);
    bDisableSignatureCheck		= ini.ReadBoolean	("Patches",				"PakLoader",				true);
    // Features
    bHookUE						= ini.ReadBoolean	("Features",			"HookUE",					true);
    bDialog						= ini.ReadBoolean	("Features",			"Dialog",					true);
    bNotifs						= ini.ReadBoolean	("Features",			"Notifications",			true);

    // Patterns
    pSigCheck					= ini.ReadString	("Patterns",			"SigCheck",					"");
    pEndpointLoader				= ini.ReadString	("Patterns",			"EndpointLoader",			"");
    pProdEndpointLoader			= ini.ReadString	("Patterns",			"ProdEndpointLoader",		"");
    pSunsetDate					= ini.ReadString	("Patterns",			"SunsetDate",				"");
    // Features
    pFText						= ini.ReadString	("Patterns.UE",			"FText",					"");
    pCFName						= ini.ReadString	("Patterns.UE",			"CFName",					"");
    pWCFname					= ini.ReadString	("Patterns.UE",			"WCFName",					"");
    pDialog						= ini.ReadString	("Patterns.MVS",		"Dialog",					"");
    pDialogParams				= ini.ReadString	("Patterns.MVS",		"DialogParams",				"");
    pDialogCallback				= ini.ReadString	("Patterns.MVS",		"DialogCallback",			"");
    pQuitGameCallback			= ini.ReadString	("Patterns.MVS",		"QuitGameCallback",			"");
    pFighterInstance			= ini.ReadString	("Patterns.MVS",		"FighterInstance",			"");
    pNotifs						= ini.ReadString	("Patterns.MVS",		"Notifications",			"");
    

    // Private Server
    szServerUrl					= ini.ReadString	("Server.Game",			"ServerUrl",				"");
    szProdServerUrl				= ini.ReadString	("Server.Prod",			"ServerUrl",				"");
    bEnableServerProxy			= ini.ReadBoolean	("Server.Game",			"Enabled",					false);
    bEnableProdServerProxy		= ini.ReadBoolean	("Server.Prod",			"Enabled",					false);
}
