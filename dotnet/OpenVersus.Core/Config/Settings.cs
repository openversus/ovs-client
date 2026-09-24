using System.Globalization;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Config;

public enum SettingKind
{
    Bool, Int, String
}

/// <summary>One row of the settings table: where it lives in the ini and its default.</summary>
public sealed record SettingDef(string Name, string Section, string Key, SettingKind Kind, string Default)
{
    public static SettingDef Bool(string name, string section, string key, bool value) => new(name, section, key, SettingKind.Bool, value ? "true" : "false");
    public static SettingDef Int(string name, string section, string key, ulong value) => new(name, section, key, SettingKind.Int, value.ToString(CultureInfo.InvariantCulture));
    public static SettingDef Str(string name, string section, string key, string value) => new(name, section, key, SettingKind.String, value);
}

/// <summary>
/// OpenVersus.ini. Every row is read, and a row the file lacks is added with its default, so a
/// fresh install gets a complete file and a file missing a new key gains it on the next run.
/// Nothing already in the file is rewritten: the player's values, spacing, blank lines and
/// comments stay as they are, and a value that does not parse is logged and read as its default
/// rather than replaced. Booleans are true/false, on/off or 1/0 in any case.
/// The table is the C++ OVSDefaultSettingsArray in its order, with the rows this port adds at
/// the end of their sections. Values are looked up by the row's name so the table is the only list.
/// </summary>
public sealed class Settings
{
    public const string FileName = "OpenVersus.ini";

    public static readonly IReadOnlyList<SettingDef> Table =
    [
        // Debug
        SettingDef.Bool("bEnableConsoleWindow", "Settings.Debug", "ShowConsole", true),
        SettingDef.Bool("bPauseOnStart", "Settings.Debug", "DebugPause", false),
        SettingDef.Bool("bDebug", "Settings.Debug", "DebugLogging", false),
        SettingDef.Bool("bAllowNonMVS", "Settings.Debug", "NonMVSPatching", false),
        SettingDef.Bool("bCountSunsetCalls", "Settings.Debug", "CountSunsetCalls", false),
        // Settings
        SettingDef.Int("iLogSize", "Settings", "LogSize", 50),
        // A level name (trace, debug, info, warn, error, critical, none) or number; "0", which
        // existing files carry from the C++ client, means "decide from DebugLogging".
        SettingDef.Str("iLogLevel", "Settings", "LogLevel", "0"),
        SettingDef.Str("szModLoader", "Settings", "ModLoader", "Kernel32.CreateFileW"),
        SettingDef.Str("szAntiCheatEngine", "Settings", "AntiCheatEngine", "User32.EnumChildWindows"),
        SettingDef.Str("szCurlSetOpt", "Settings", "CurlSetOpt", "libcurl.curl_easy_setopt"),
        SettingDef.Str("szCurlPerform", "Settings", "CurlPerform", "libcurl.curl_easy_perform"),
        SettingDef.Bool("bEnableKeyboardHotkeys", "Settings", "EnableKeyboardHotkeys", true),
        SettingDef.Bool("bAutoUpdate", "Settings", "AutoUpdate", true),
        // Keybinds
        SettingDef.Str("hkMenu", "Settings.Keybinds", "ToggleMenu", "F1"),
        // Patches
        SettingDef.Bool("bSunsetDate", "Patches", "SunsetDate", true),
        SettingDef.Bool("bDisableSignatureCheck", "Patches", "PakLoader", true),
        SettingDef.Bool("bPostMatchFreeze", "Patches", "PostMatchFreeze", true),
        SettingDef.Bool("bSunsetCallers", "Patches", "SunsetCallers", false),
        // Features
        SettingDef.Bool("bHookUE", "Features", "HookUE", true),
        SettingDef.Bool("bDialog", "Features", "Dialog", true),
        SettingDef.Bool("bNotifs", "Features", "Notifications", true),
        SettingDef.Bool("bNetStats", "Features", "NetStats", false),
        // Patterns, valid for the final patch of the game (Unreal Engine 5.1.1.0)
        SettingDef.Str("pSigCheck", "Patterns", "SigCheck", "48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC 48 83 EC 28 E8 ? ? ? ? 48 89 05 ? ? ? ? 48 83 C4 28 C3 CC CC CC CC CC CC CC CC CC CC CC 48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC 48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC"),
        SettingDef.Str("pEndpointLoader", "Patterns", "EndpointLoader", "48 8b cb 48 8d 15 ? ? ? ? E8 ? ? ? ? 48 8b 4c 24 ? 48 85 c9 74 05 E8 ? ? ? ? 48 8b c3 48 8b 5c 24"),
        SettingDef.Str("pProdEndpointLoader", "Patterns", "ProdEndpointLoader", "49 ? ? 48 8d 15 ? ? ? ? E8 ? ? ? ? 49 ? ? ? ? 00 00 48 8b"),
        SettingDef.Str("pSunsetDate", "Patterns", "SunsetDate", "48 8d 0d ? ? ? ? e8 ? ? ? ? 83 3D ? ? ? ? FF 75 ? 89 7C"),
        SettingDef.Str("pFText", "Patterns.UE", "FText", "4C 8B ? E8 ? ? ? ? 4C 8D ? ? ? ? ? 4C 8B ? 4C 8D ? ? ? ? ? 48 8D ? ? ? ? ? 48 8D ? ? E8 ? ? ? ? 48 8B ? 48 8D ? 00 E8 ? ? ? ? 49 8B ? 00 4C 8B ? 48 89 ? ? ? 00 00 49 8B ? ? C7 85 ? ? ? ? 04 00 00 00"),
        SettingDef.Str("pCFName", "Patterns.UE", "CFName", "4C 8D 9C ? ? ? ? ? 49 8B ? ? 49 8B ? ? 4D 8B ? ? 4D 8B ? ? 41 ? ? ? ? 49 8B ? ? C3 48 8D ? ? ? ? ? E8 ? ? ? ? 83 3D ? ? ? ? FF 0F 85 ? ? ? ? 41 B8 01 00 00 00 48 8D ? ? ? ? ? 48 8D ? ? ? ? ? E8 ? ? ? ? 48 8D ? ? ? ? ? E8 ? ? ? ? E9 ? ? ? ? CC CC CC CC CC CC CC CC"),
        SettingDef.Str("pWCFname", "Patterns.UE", "WCFName", "48 85 ? 74 1E 0F ? ? 66 85 C0 74 16 0F ? ?"),
        SettingDef.Str("pDialog", "Patterns.MVS", "Dialog", "40 ? 48 83 ? ? 48 ? ? E8 ? ? ? ? 48 85 ? 75 ? 48 8B ? 48"),
        SettingDef.Str("pDialogParams", "Patterns.MVS", "DialogParams", "48 89 ? ? ? 48 89 ? ? ? 48 89 ? ? ? 48 89 ? ? ? 41 ? 48 83 ? ? 41 0F ? ? 48 ? ? 48 ? ? E8 ? ? ? ? 48"),
        SettingDef.Str("pDialogCallback", "Patterns.MVS", "DialogCallback", "E8 ? ? ? ? 48 8D 15 ? ? ? ? 48 8D 4C ? ? E8 ? ? ? ? 4C ? ? ? ? C6 ? ? ? 00 48 8D ? ? ? 49 8B CE"),
        SettingDef.Str("pQuitGameCallback", "Patterns.MVS", "QuitGameCallback", "40 ? 48 83 ? ? 48 8B ? 48 83 ? ? E8 ? ? ? ? 84 C0 74 19 48 8B ? ? 45 33 C9 45 33 C0"),
        SettingDef.Str("pFighterInstance", "Patterns.MVS", "FighterInstance", "48 8D 05 ? ? ? 00 49 89 ? ? 48 8D ? ? ? ? ? 49 89 ? ? 49 C7 ? ? 00 00 00 00 C7 44 ? ? 08 00 00 10 C7 44 ? ? 08 00 00 00 C7 44 ? ? 70 08 00 00"),
        SettingDef.Str("pNotifs", "Patterns.MVS", "Notifications", "FF 50 ? F3 0F 10 ? ? ? ? ? 48 8B ? F3 0F ? ? ? E8"),
        SettingDef.Str("pPostMatchFreeze", "Patterns.MVS", "PostMatchFreeze", "45 3B F8 75 11 0F B6 83 F1 05 00 00 2C 07 3C 02 0F 86 ? ? ? ?"),
        // Servers
        SettingDef.Str("szServerUrl", "Server.Game", "ServerUrl", OvsVersion.DefaultServerUrl),
        SettingDef.Str("szProdServerUrl", "Server.Prod", "ServerUrl", OvsVersion.DefaultServerUrl),
        SettingDef.Bool("bEnableServerProxy", "Server.Game", "Enabled", true),
        SettingDef.Bool("bEnableProdServerProxy", "Server.Prod", "Enabled", true),
    ];

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string Path { get; }

    private Settings(string path) => Path = path;

    /// <summary>Reads every row, adding the missing ones to the file with their defaults.</summary>
    public static Settings Load(string path, ILogger? log = null)
    {
        var ini = IniFile.Load(path);
        if (ini.LoadError != null)
        {
            log?.LogWarning("[Settings] Could not read {Path}, using defaults: {Error}", path, ini.LoadError.Message);
        }

        var settings = new Settings(path);
        foreach (var def in Table)
        {
            string? value = ini.Get(def.Section, def.Key);
            if (value == null)
            {
                value = def.Default;
                ini.Set(def.Section, def.Key, value);
            }

            settings._values[def.Name] = Normalize(def, value, log);
        }

        if (ini.Save())
        {
            log?.LogInformation("[Settings] Added missing keys to {Path}", path);
        }
        else if (ini.SaveError != null)
        {
            log?.LogWarning("[Settings] Could not write {Path}, continuing with the values read: {Error}", path, ini.SaveError.Message);
        }

        return settings;
    }

    /// <summary>Values without touching the disk, for tests.</summary>
    public static Settings FromValues(IReadOnlyDictionary<string, string> values)
    {
        var settings = new Settings("");
        foreach (var def in Table)
        {
            settings._values[def.Name] = values.TryGetValue(def.Name, out string? v) ? Normalize(def, v, null) : def.Default;
        }

        return settings;
    }

    /// <summary>The value if it parses as the row's kind, else the row's default, with a warning.</summary>
    private static string Normalize(SettingDef def, string value, ILogger? log)
    {
        switch (def.Kind)
        {
            case SettingKind.Bool when !IniFile.TryParseBool(value, out _):
                log?.LogWarning("[Settings] [{Section}] {Key} = \"{Value}\" is not true/false, on/off or 1/0; using {Default}", def.Section, def.Key, value, def.Default);
                return def.Default;
            case SettingKind.Int when !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _):
                log?.LogWarning("[Settings] [{Section}] {Key} = \"{Value}\" is not a whole number; using {Default}", def.Section, def.Key, value, def.Default);
                return def.Default;
            default:
                return value;
        }
    }

    public string String(string name) => _values[name];
    public bool Bool(string name) => IniFile.TryParseBool(_values[name], out bool value) && value;
    public ulong Int(string name) => ulong.TryParse(_values[name], NumberStyles.None, CultureInfo.InvariantCulture, out ulong v) ? v : 0;

    // Debug
    public bool EnableConsoleWindow => Bool("bEnableConsoleWindow");
    public bool PauseOnStart => Bool("bPauseOnStart");
    public bool Debug => Bool("bDebug");
    public string LogLevel => String("iLogLevel");
    public bool AllowNonMvs => Bool("bAllowNonMVS");
    public bool CountSunsetCalls => Bool("bCountSunsetCalls");
    // Settings
    public bool EnableKeyboardHotkeys => Bool("bEnableKeyboardHotkeys");
    public bool AutoUpdate => Bool("bAutoUpdate");
    public string MenuHotkey => String("hkMenu");
    // Patches
    public bool SunsetDate => Bool("bSunsetDate");
    public bool DisableSignatureCheck => Bool("bDisableSignatureCheck");
    public bool PostMatchFreeze => Bool("bPostMatchFreeze");
    public bool SunsetCallers => Bool("bSunsetCallers");
    // Features
    public bool HookUe => Bool("bHookUE");
    public bool Dialog => Bool("bDialog");
    public bool Notifications => Bool("bNotifs");
    public bool NetStats => Bool("bNetStats");
    // Servers
    public string ServerUrl => String("szServerUrl");
    public string ProdServerUrl => String("szProdServerUrl");
    public bool EnableServerProxy => Bool("bEnableServerProxy");
    public bool EnableProdServerProxy => Bool("bEnableProdServerProxy");
    // Patterns, by their row name ("pSigCheck" and so on)
    public string Pattern(string name) => String(name);
}
