using System.Globalization;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Config;

/// <summary>How a row's value is parsed.</summary>
public enum SettingKind
{
    /// <summary>true/false, on/off or 1/0, in any case.</summary>
    Bool,
    /// <summary>A whole number.</summary>
    Int,
    /// <summary>Any text.</summary>
    String,
    /// <summary>A byte pattern, as <see cref="Memory.BytePattern"/> parses it.</summary>
    Pattern,
}

/// <summary>One row of the settings table: where it lives in the file and its default.</summary>
/// <param name="Section">The table (the ini's section).</param>
/// <param name="Key">The key within the section.</param>
/// <param name="Kind">How the value is parsed.</param>
/// <param name="Default">The value used, and written to the file, when the key is missing or does not parse.</param>
public sealed record SettingDef(string Section, string Key, SettingKind Kind, string Default)
{
    /// <summary>A true/false row.</summary>
    public static SettingDef Bool(string section, string key, bool value) => new(section, key, SettingKind.Bool, value ? "true" : "false");
    /// <summary>A whole-number row.</summary>
    public static SettingDef Int(string section, string key, ulong value) => new(section, key, SettingKind.Int, value.ToString(CultureInfo.InvariantCulture));
    /// <summary>A text row.</summary>
    public static SettingDef Str(string section, string key, string value) => new(section, key, SettingKind.String, value);
    /// <summary>A byte-pattern row.</summary>
    public static SettingDef Pattern(string section, string key, string value) => new(section, key, SettingKind.Pattern, value);

    /// <summary>"[Section] Key", as the log names a row.</summary>
    public override string ToString() => $"[{Section}] {Key}";
}

/// <summary>Something wrong with the settings files that the player should hear about in the game.</summary>
/// <param name="Title">The toast's text.</param>
/// <param name="Detail">The toast's caption.</param>
public sealed record SettingsProblem(string Title, string Detail);

/// <summary>One key the client no longer reads, and why, so a file that has it is told so.</summary>
/// <param name="Section">The table.</param>
/// <param name="Key">The key.</param>
/// <param name="Reason">Why it is no longer read.</param>
public sealed record RetiredSetting(string Section, string Key, string Reason);

/// <summary>
/// OpenVersus.toml. Every row is read, and a row the file lacks is added with its default, so a
/// fresh install gets a complete file and a file missing a new key gains it on the next run.
/// Nothing already in the file is rewritten: the player's values, spacing, blank lines and
/// comments stay as they are, and a value that does not parse is logged and read as its default
/// rather than replaced. A file that is not valid TOML is logged with where it broke, read as all
/// defaults, and left alone. A legacy OpenVersus.ini with no OpenVersus.toml beside it is
/// converted once (<see cref="SettingsMigration"/>) and deleted.
/// The table is the C++ OVSDefaultSettingsArray in its order, with the rows this port adds at
/// the end of their sections and [Settings.Debug] moved down to just above the servers; the typed
/// properties below read it, so nothing else names a key.
/// </summary>
public sealed class Settings
{
    /// <summary>The settings file's name, beside the plugin.</summary>
    public const string FileName = "OpenVersus.toml";

    /// <summary>The settings file the C++ client and earlier versions of this one used.</summary>
    public const string LegacyFileName = "OpenVersus.ini";

    /// <summary>The rows, one static field each, so a property refers to its row rather than to a string.</summary>
    public static class Rows
    {
        // Debug
        /// <summary>Opens a console window, which the log is also written to.</summary>
        public static readonly SettingDef ShowConsole = SettingDef.Bool("Settings.Debug", "ShowConsole", true);
        /// <summary>Holds startup at a message box until it is dismissed.</summary>
        public static readonly SettingDef DebugPause = SettingDef.Bool("Settings.Debug", "DebugPause", false);
        /// <summary>With <see cref="LogLevel"/> at 0: Debug logging when on, Information when off.</summary>
        public static readonly SettingDef DebugLogging = SettingDef.Bool("Settings.Debug", "DebugLogging", false);
        /// <summary>Silences the warning box shown when the host process is not MultiVersus. The client loads either way.</summary>
        public static readonly SettingDef NonMvsPatching = SettingDef.Bool("Settings.Debug", "NonMVSPatching", false);
        /// <summary>Routes the sunset check's comparison through a counter, which the heartbeat reports each minute.</summary>
        public static readonly SettingDef CountSunsetCalls = SettingDef.Bool("Settings.Debug", "CountSunsetCalls", false);
        // Settings
        /// <summary>
        /// The minimum log level: a name (trace, debug, info, warn, error, critical, none, or an alias such
        /// as verbose) or a number from 1 (debug) to 6 (none). "0", which existing files carry from the C++
        /// client, means "decide from <see cref="DebugLogging"/>".
        /// </summary>
        public static readonly SettingDef LogLevel = SettingDef.Str("Settings", "LogLevel", "info");
        /// <summary>Installs the keyboard hook, which only logs F1 presses (<see cref="Hooks.KeyboardHook"/>).</summary>
        public static readonly SettingDef EnableKeyboardHotkeys = SettingDef.Bool("Settings", "EnableKeyboardHotkeys", true);
        /// <summary>Checks the server for a newer client and installs it (<see cref="Net.AutoUpdate"/>).</summary>
        public static readonly SettingDef AutoUpdate = SettingDef.Bool("Settings", "AutoUpdate", true);
        // Keybinds
        /// <summary>Read so existing files keep it; nothing uses it. The keyboard hook watches F1 regardless.</summary>
        public static readonly SettingDef ToggleMenu = SettingDef.Str("Settings.Keybinds", "ToggleMenu", "F1");
        // Patches
        /// <summary>Makes the game's sunset-date check return false (<see cref="Hooks.SunsetPatch"/>).</summary>
        public static readonly SettingDef SunsetDate = SettingDef.Bool("Patches", "SunsetDate", true);
        /// <summary>Lets .pak and .utoc files with bad signatures load (<see cref="Hooks.SigCheckPatch"/>).</summary>
        public static readonly SettingDef PakLoader = SettingDef.Bool("Patches", "PakLoader", true);
        /// <summary>Lets the other clients see the winner during the post-match linger (<see cref="Hooks.PostMatchFreezePatch"/>).</summary>
        public static readonly SettingDef PostMatchFreeze = SettingDef.Bool("Patches", "PostMatchFreeze", true);
        /// <summary>Patches out every call to the sunset check, so it is never entered (<see cref="Hooks.SunsetCallersPatch"/>). Needs <see cref="SunsetDate"/>.</summary>
        public static readonly SettingDef SunsetCallers = SettingDef.Bool("Patches", "SunsetCallers", false);
        // Features
        /// <summary>Resolves the engine functions and records game instances (<see cref="Hooks.UeFunctionHooks"/>). Dialogs, notifications and the object array need it.</summary>
        public static readonly SettingDef HookUe = SettingDef.Bool("Features", "HookUE", true);
        /// <summary>Resolves the dialog functions, which the free-mod notice uses (<see cref="Hooks.DialogHooks"/>).</summary>
        public static readonly SettingDef Dialog = SettingDef.Bool("Features", "Dialog", true);
        /// <summary>Resolves the notification functions (<see cref="Hooks.NotificationHooks"/>).</summary>
        public static readonly SettingDef Notifications = SettingDef.Bool("Features", "Notifications", true);
        /// <summary>Logs the rollback session's stats once a second during a match, one file per match (<see cref="OpenVersus.NetStats.NetStatsLogger"/>).</summary>
        public static readonly SettingDef NetStats = SettingDef.Bool("Features", "NetStats", false);
        // Patterns, valid for the final patch of the game (Unreal Engine 5.1.1.0)
        /// <summary>Finds the pak signature check's tail jump, for <see cref="Hooks.SigCheckPatch"/>.</summary>
        public static readonly SettingDef SigCheckPattern = SettingDef.Pattern("Patterns", "SigCheck", "48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC 48 83 EC 28 E8 ? ? ? ? 48 89 05 ? ? ? ? 48 83 C4 28 C3 CC CC CC CC CC CC CC CC CC CC CC 48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC 48 8D 0D ? ? ? ? E9 ? ? ? ? CC CC CC CC");
        /// <summary>Finds the call that stores the game-server endpoint, for <see cref="Hooks.EndpointHooks.ApplyGame"/>.</summary>
        public static readonly SettingDef EndpointLoaderPattern = SettingDef.Pattern("Patterns", "EndpointLoader", "48 8b cb 48 8d 15 ? ? ? ? E8 ? ? ? ? 48 8b 4c 24 ? 48 85 c9 74 05 E8 ? ? ? ? 48 8b c3 48 8b 5c 24");
        /// <summary>Finds the call that stores the prod endpoint, for <see cref="Hooks.EndpointHooks.ApplyProd"/>.</summary>
        public static readonly SettingDef ProdEndpointLoaderPattern = SettingDef.Pattern("Patterns", "ProdEndpointLoader", "49 ? ? 48 8d 15 ? ? ? ? E8 ? ? ? ? 49 ? ? ? ? 00 00 48 8b");
        /// <summary>Finds the sunset-date check, for <see cref="Hooks.SunsetPatch"/>.</summary>
        public static readonly SettingDef SunsetDatePattern = SettingDef.Pattern("Patterns", "SunsetDate", "48 8d 0d ? ? ? ? e8 ? ? ? ? 83 3D ? ? ? ? FF 75 ? 89 7C");
        /// <summary>Finds FText::FromName, FText::FromString, FText::GetEmpty and FName::ToString by the calls around it, for <see cref="Hooks.UeFunctionHooks"/>.</summary>
        public static readonly SettingDef FTextPattern = SettingDef.Pattern("Patterns.UE", "FText", "4C 8B ? E8 ? ? ? ? 4C 8D ? ? ? ? ? 4C 8B ? 4C 8D ? ? ? ? ? 48 8D ? ? ? ? ? 48 8D ? ? E8 ? ? ? ? 48 8B ? 48 8D ? 00 E8 ? ? ? ? 49 8B ? 00 4C 8B ? 48 89 ? ? ? 00 00 49 8B ? ? C7 85 ? ? ? ? 04 00 00 00");
        /// <summary>Finds the narrow FName constructor, for <see cref="Hooks.UeFunctionHooks"/>.</summary>
        public static readonly SettingDef CFNamePattern = SettingDef.Pattern("Patterns.UE", "CFName", "4C 8D 9C ? ? ? ? ? 49 8B ? ? 49 8B ? ? 4D 8B ? ? 4D 8B ? ? 41 ? ? ? ? 49 8B ? ? C3 48 8D ? ? ? ? ? E8 ? ? ? ? 83 3D ? ? ? ? FF 0F 85 ? ? ? ? 41 B8 01 00 00 00 48 8D ? ? ? ? ? 48 8D ? ? ? ? ? E8 ? ? ? ? 48 8D ? ? ? ? ? E8 ? ? ? ? E9 ? ? ? ? CC CC CC CC CC CC CC CC");
        /// <summary>Finds the wide FName constructor, for <see cref="Hooks.UeFunctionHooks"/>.</summary>
        public static readonly SettingDef WCFNamePattern = SettingDef.Pattern("Patterns.UE", "WCFName", "48 85 ? 74 1E 0F ? ? 66 85 C0 74 16 0F ? ?");
        /// <summary>Finds UMvsFrontendManager::AddDialog and the frontend manager getter, for <see cref="Hooks.DialogHooks"/>.</summary>
        public static readonly SettingDef DialogPattern = SettingDef.Pattern("Patterns.MVS", "Dialog", "40 ? 48 83 ? ? 48 ? ? E8 ? ? ? ? 48 85 ? 75 ? 48 8B ? 48");
        /// <summary>Finds the FMvsDialogParameters constructor, for <see cref="Hooks.DialogHooks"/>; resolved for the log only.</summary>
        public static readonly SettingDef DialogParamsPattern = SettingDef.Pattern("Patterns.MVS", "DialogParams", "48 89 ? ? ? 48 89 ? ? ? 48 89 ? ? ? 48 89 ? ? ? 41 ? 48 83 ? ? 41 0F ? ? 48 ? ? 48 ? ? E8 ? ? ? ? 48");
        /// <summary>Finds the call to the dialog button callback setter, for <see cref="Hooks.DialogHooks"/>.</summary>
        public static readonly SettingDef DialogCallbackPattern = SettingDef.Pattern("Patterns.MVS", "DialogCallback", "E8 ? ? ? ? 48 8D 15 ? ? ? ? 48 8D 4C ? ? E8 ? ? ? ? 4C ? ? ? ? C6 ? ? ? 00 48 8D ? ? ? 49 8B CE");
        /// <summary>Finds the game's quit callback. Nothing calls it now; it stays resolved for dialogs that should offer to quit (declining an update, say).</summary>
        public static readonly SettingDef QuitGameCallbackPattern = SettingDef.Pattern("Patterns.MVS", "QuitGameCallback", "40 ? 48 83 ? ? 48 8B ? 48 83 ? ? E8 ? ? ? ? 84 C0 74 19 48 8B ? ? 45 33 C9 45 33 C0");
        /// <summary>Finds the UFighterGameInstance constructor's tail jump, for <see cref="Hooks.UeFunctionHooks"/>.</summary>
        public static readonly SettingDef FighterInstancePattern = SettingDef.Pattern("Patterns.MVS", "FighterInstance", "48 8D 05 ? ? ? 00 49 89 ? ? 48 8D ? ? ? ? ? 49 89 ? ? 49 C7 ? ? 00 00 00 00 C7 44 ? ? 08 00 00 10 C7 44 ? ? 08 00 00 00 C7 44 ? ? 70 08 00 00");
        /// <summary>Finds UMvsNotificationManager::RequestShowNotification and UMvsNotificationManager::Get, for <see cref="Hooks.NotificationHooks"/>.</summary>
        public static readonly SettingDef NotificationsPattern = SettingDef.Pattern("Patterns.MVS", "Notifications", "FF 50 ? F3 0F 10 ? ? ? ? ? 48 8B ? F3 0F ? ? ? E8");
        /// <summary>Finds the post-match early return, for <see cref="Hooks.PostMatchFreezePatch"/>.</summary>
        public static readonly SettingDef PostMatchFreezePattern = SettingDef.Pattern("Patterns.MVS", "PostMatchFreeze", "45 3B F8 75 11 0F B6 83 F1 05 00 00 2C 07 3C 02 0F 86 ? ? ? ?");
        // Servers
        /// <summary>The OpenVersus server: the game-server endpoint, and where the notification poller, identity registration and update check go.</summary>
        public static readonly SettingDef ServerUrl = SettingDef.Str("Server.Game", "ServerUrl", OvsVersion.DefaultServerUrl);
        /// <summary>The server the game's prod (WB network) endpoint is pointed at.</summary>
        public static readonly SettingDef ProdServerUrl = SettingDef.Str("Server.Prod", "ServerUrl", OvsVersion.DefaultServerUrl);
        /// <summary>Points the game-server endpoint at <see cref="ServerUrl"/>, and runs the notification poller.</summary>
        public static readonly SettingDef EnableServerProxy = SettingDef.Bool("Server.Game", "Enabled", true);
        /// <summary>Points the prod endpoint at <see cref="ProdServerUrl"/>.</summary>
        public static readonly SettingDef EnableProdServerProxy = SettingDef.Bool("Server.Prod", "Enabled", true);
    }

    /// <summary>Every row in file order; the order a new file is written in. [Settings.Debug]
    /// sits just above the servers, below what players change.</summary>
    public static readonly IReadOnlyList<SettingDef> Table =
    [
        Rows.LogLevel,
        Rows.EnableKeyboardHotkeys,
        Rows.AutoUpdate,
        Rows.ToggleMenu,
        Rows.SunsetDate,
        Rows.PakLoader,
        Rows.PostMatchFreeze,
        Rows.SunsetCallers,
        Rows.HookUe,
        Rows.Dialog,
        Rows.Notifications,
        Rows.NetStats,
        Rows.SigCheckPattern,
        Rows.EndpointLoaderPattern,
        Rows.ProdEndpointLoaderPattern,
        Rows.SunsetDatePattern,
        Rows.FTextPattern,
        Rows.CFNamePattern,
        Rows.WCFNamePattern,
        Rows.DialogPattern,
        Rows.DialogParamsPattern,
        Rows.DialogCallbackPattern,
        Rows.QuitGameCallbackPattern,
        Rows.FighterInstancePattern,
        Rows.NotificationsPattern,
        Rows.PostMatchFreezePattern,
        Rows.ShowConsole,
        Rows.DebugPause,
        Rows.DebugLogging,
        Rows.NonMvsPatching,
        Rows.CountSunsetCalls,
        Rows.ServerUrl,
        Rows.ProdServerUrl,
        Rows.EnableServerProxy,
        Rows.EnableProdServerProxy,
    ];

    /// <summary>Keys the C++ client read and this one does not.</summary>
    public static readonly IReadOnlyList<RetiredSetting> RetiredKeys =
    [
        new("Settings", "LogSize", "logs/OpenVersus.log starts fresh each launch and older logs are archived, so there is no size to cap"),
        new("Settings", "ModLoader", "it named a function the C++ client looked up, and this client does not use it"),
        new("Settings", "AntiCheatEngine", "it named a function the C++ client looked up, and this client does not use it"),
        new("Settings", "CurlSetOpt", "it named a function the C++ client looked up, and this client does not use it"),
        new("Settings", "CurlPerform", "it named a function the C++ client looked up, and this client does not use it"),
    ];

    /// <summary>The row at <paramref name="section"/> and <paramref name="key"/>, regardless of case, or null.</summary>
    public static SettingDef? Row(string section, string key) =>
        Table.FirstOrDefault(d => string.Equals(d.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The retired key at <paramref name="section"/> and <paramref name="key"/>, regardless of case, or null.</summary>
    public static RetiredSetting? Retired(string section, string key) =>
        RetiredKeys.FirstOrDefault(r => string.Equals(r.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    private readonly Dictionary<SettingDef, string> _values = new();

    /// <summary>The file the settings were read from; empty for <see cref="FromValues"/>.</summary>
    public string Path { get; }

    /// <summary>What went wrong reading the settings, for the startup toast, or null.</summary>
    public SettingsProblem? Problem { get; private set; }

    private Settings(string path) => Path = path;

    /// <summary>
    /// Reads <see cref="FileName"/> in <paramref name="directory"/>, converting a legacy
    /// <see cref="LegacyFileName"/> first when that is all there is, and adds the missing rows to
    /// the file with their defaults.
    /// </summary>
    public static Settings Load(string directory, ILogger? log = null)
    {
        string path = System.IO.Path.Combine(directory, FileName);
        string legacyPath = System.IO.Path.Combine(directory, LegacyFileName);
        bool retireLegacy = false;
        SettingsProblem? problem = null;
        TomlConfig toml;
        if (File.Exists(path))
        {
            toml = TomlConfig.Load(path);
            if (File.Exists(legacyPath))
            {
                retireLegacy = Merge(legacyPath, toml, log);
            }
        }
        else if (File.Exists(legacyPath))
        {
            toml = Convert(legacyPath, path, log, out retireLegacy, out problem);
        }
        else
        {
            toml = TomlConfig.Load(path);
        }

        if (toml.LoadError != null)
        {
            log?.LogWarning("[Settings] Could not read {Path}, using defaults: {Error}", path, toml.LoadError.Message);
            problem = new($"{FileName} could not be read", "Using default settings; see logs/OpenVersus.log");
        }
        else if (toml.Errors.Count > 0)
        {
            log?.LogWarning("[Settings] {Path} is not valid TOML, so every setting is at its default and the file is left as it is. {Errors}",
                path, string.Join("; ", toml.Errors.Take(5)));
            string where = toml.Errors[0].Split(':')[0];
            problem = new($"{FileName} has a mistake", $"{char.ToUpperInvariant(where[0])}{where[1..]}. Using default settings until it is fixed");
        }

        var settings = new Settings(path) { Problem = problem };
        foreach (var def in Table)
        {
            string? value = toml.Get(def.Section, def.Key);
            if (value == null)
            {
                value = def.Default;
                toml.Add(def.Section, def.Key, def.Kind, value);
            }

            settings._values[def] = Normalize(def, value, log);
        }

        foreach ((string section, string key) in toml.Keys())
        {
            if (Row(section, key) != null)
            {
                continue;
            }

            RetiredSetting? retired = Retired(section, key);
            if (retired != null)
            {
                log?.LogInformation("[Settings] [{Section}] {Key} is no longer used and can be deleted: {Reason}", retired.Section, retired.Key, retired.Reason);
            }
            else
            {
                log?.LogWarning("[Settings] [{Section}] {Key} is not a setting this version knows; it is ignored", section, key);
            }
        }

        if (toml.Save())
        {
            log?.LogInformation("[Settings] Wrote {Path}", path);
        }
        else if (toml.SaveError != null)
        {
            log?.LogWarning("[Settings] Could not write {Path}, continuing with the values read: {Error}", path, toml.SaveError.Message);
        }

        // The ini goes only once the TOML is on disk with everything taken from it; until then it
        // is the only copy.
        if (retireLegacy && File.Exists(path) && toml.SaveError == null)
        {
            try
            {
                File.Delete(legacyPath);
                log?.LogInformation("[Settings] Deleted {Legacy}", legacyPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("[Settings] Could not delete {Legacy}; it is ignored from now on: {Error}", legacyPath, e.Message);
            }
        }

        return settings;
    }

    /// <summary>
    /// A legacy ini beside an existing TOML, as when a new release is extracted over an install
    /// that never converted: its settings are merged in (<see cref="SettingsMigration.Merge"/>).
    /// Returns whether the ini can be deleted; not when either file cannot be read, since the TOML
    /// could then not take what the ini holds.
    /// </summary>
    private static bool Merge(string legacyPath, TomlConfig toml, ILogger? log)
    {
        if (toml.LoadError != null || toml.Errors.Count > 0)
        {
            log?.LogWarning("[Settings] {Legacy} is left as it is until {File} can be read", LegacyFileName, FileName);
            return false;
        }

        var ini = IniFile.Load(legacyPath);
        if (ini.LoadError != null)
        {
            log?.LogWarning("[Settings] Could not read {Legacy} to merge it into {File}: {Error}", legacyPath, FileName, ini.LoadError.Message);
            return false;
        }

        int merged = SettingsMigration.Merge(ini, toml, log);
        log?.LogWarning("[Settings] Found {Legacy} beside {File}: took {Count} setting(s) from it where the TOML still had the default (never ServerUrl); the ini is deleted once the TOML is saved",
            LegacyFileName, FileName, merged);
        return true;
    }

    /// <summary>
    /// The legacy ini as TOML, not yet written. When the conversion does not check out, the result
    /// is an empty document, so every row takes its default, and the log keeps the old file's
    /// text. When the ini cannot be read at all, nothing is written and nothing deleted: this run
    /// uses the defaults and the next one tries again, since the ini is the only copy.
    /// </summary>
    private static TomlConfig Convert(string legacyPath, string path, ILogger? log, out bool retireLegacy, out SettingsProblem? problem)
    {
        var ini = IniFile.Load(legacyPath);
        problem = null;
        if (ini.LoadError != null)
        {
            log?.LogWarning("[Settings] Could not read {Legacy}, so this run uses the defaults and it is tried again next launch: {Error}", legacyPath, ini.LoadError.Message);
            retireLegacy = false;
            problem = new($"{LegacyFileName} could not be read", "Using default settings this time; see logs/OpenVersus.log");
            return TomlConfig.InMemory(path);
        }

        retireLegacy = true;

        string text = SettingsMigration.Convert(ini, log);
        string? failure = SettingsMigration.Verify(ini, text);
        if (failure != null)
        {
            log?.LogWarning("[Settings] Could not convert {Legacy} ({Failure}), so the new {File} starts from defaults. The old file was:{NewLine}{Text}",
                legacyPath, failure, FileName, Environment.NewLine, ini.ToString());
            problem = new("Settings reset to defaults", $"Your old {LegacyFileName} could not be converted; see logs/OpenVersus.log");
            return TomlConfig.Load(path);
        }

        log?.LogInformation("[Settings] Converted {Legacy} to {File}", legacyPath, FileName);
        return TomlConfig.FromText(text, path);
    }

    /// <summary>Values without touching the disk, for tests; rows not given take their defaults.</summary>
    public static Settings FromValues(IReadOnlyDictionary<SettingDef, string> values)
    {
        var settings = new Settings("");
        foreach (var def in Table)
        {
            settings._values[def] = values.TryGetValue(def, out string? v) ? Normalize(def, v, null) : def.Default;
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

    private string Get(SettingDef def) => _values[def];
    private bool GetBool(SettingDef def) => IniFile.TryParseBool(_values[def], out bool value) && value;
    private ulong GetInt(SettingDef def) => ulong.Parse(_values[def], NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>The byte pattern under <paramref name="key"/> in whichever [Patterns*] section holds it.</summary>
    public string Pattern(string key)
    {
        foreach (var def in Table)
        {
            if (def.Kind == SettingKind.Pattern && def.Key == key)
            {
                return _values[def];
            }
        }

        throw new ArgumentException($"no pattern row is keyed \"{key}\"", nameof(key));
    }

    // Debug
    /// <inheritdoc cref="Rows.ShowConsole"/>
    public bool EnableConsoleWindow => GetBool(Rows.ShowConsole);
    /// <inheritdoc cref="Rows.DebugPause"/>
    public bool PauseOnStart => GetBool(Rows.DebugPause);
    /// <inheritdoc cref="Rows.DebugLogging"/>
    public bool Debug => GetBool(Rows.DebugLogging);
    /// <inheritdoc cref="Rows.NonMvsPatching"/>
    public bool AllowNonMvs => GetBool(Rows.NonMvsPatching);
    /// <inheritdoc cref="Rows.CountSunsetCalls"/>
    public bool CountSunsetCalls => GetBool(Rows.CountSunsetCalls);
    // Settings
    /// <inheritdoc cref="Rows.LogLevel"/>
    public string LogLevel => Get(Rows.LogLevel);
    /// <inheritdoc cref="Rows.EnableKeyboardHotkeys"/>
    public bool EnableKeyboardHotkeys => GetBool(Rows.EnableKeyboardHotkeys);
    /// <inheritdoc cref="Rows.AutoUpdate"/>
    public bool AutoUpdate => GetBool(Rows.AutoUpdate);
    /// <inheritdoc cref="Rows.ToggleMenu"/>
    public string MenuHotkey => Get(Rows.ToggleMenu);
    // Patches
    /// <inheritdoc cref="Rows.SunsetDate"/>
    public bool SunsetDate => GetBool(Rows.SunsetDate);
    /// <inheritdoc cref="Rows.PakLoader"/>
    public bool DisableSignatureCheck => GetBool(Rows.PakLoader);
    /// <inheritdoc cref="Rows.PostMatchFreeze"/>
    public bool PostMatchFreeze => GetBool(Rows.PostMatchFreeze);
    /// <inheritdoc cref="Rows.SunsetCallers"/>
    public bool SunsetCallers => GetBool(Rows.SunsetCallers);
    // Features
    /// <inheritdoc cref="Rows.HookUe"/>
    public bool HookUe => GetBool(Rows.HookUe);
    /// <inheritdoc cref="Rows.Dialog"/>
    public bool Dialog => GetBool(Rows.Dialog);
    /// <inheritdoc cref="Rows.Notifications"/>
    public bool Notifications => GetBool(Rows.Notifications);
    /// <inheritdoc cref="Rows.NetStats"/>
    public bool NetStats => GetBool(Rows.NetStats);
    // Servers
    /// <inheritdoc cref="Rows.ServerUrl"/>
    public string ServerUrl => Get(Rows.ServerUrl);
    /// <inheritdoc cref="Rows.ProdServerUrl"/>
    public string ProdServerUrl => Get(Rows.ProdServerUrl);
    /// <inheritdoc cref="Rows.EnableServerProxy"/>
    public bool EnableServerProxy => GetBool(Rows.EnableServerProxy);
    /// <inheritdoc cref="Rows.EnableProdServerProxy"/>
    public bool EnableProdServerProxy => GetBool(Rows.EnableProdServerProxy);
}
