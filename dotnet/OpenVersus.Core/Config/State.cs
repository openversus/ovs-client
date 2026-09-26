namespace OpenVersus.Config;

/// <summary>
/// OVSState.toml: what the client has already told this player, and this install's random id.
/// The C++ client's OVSState.ini has its values carried over and is deleted once the TOML is on disk.
/// </summary>
public sealed class State
{
    /// <summary>The state file's name, beside the plugin.</summary>
    public const string FileName = "OVSState.toml";

    /// <summary>The state file the C++ client and earlier versions of this one used.</summary>
    public const string LegacyFileName = "OVSState.ini";

    private readonly TomlConfig _toml;
    private readonly string _path;

    /// <summary>Opens <see cref="FileName"/> in <paramref name="directory"/>, converting a legacy
    /// <see cref="LegacyFileName"/> first when that is all there is. With <paramref name="oldHome"/>
    /// (<see cref="Layout.OldHome"/>), an OVSState.toml there is moved in, and an OVSState.ini there
    /// is converted when <paramref name="directory"/> has none.</summary>
    public State(string directory, string? oldHome = null)
    {
        string path = _path = Path.Combine(directory, FileName);
        Layout.TakeOver(oldHome, directory, FileName);
        string legacyPath = Path.Combine(directory, LegacyFileName);
        if (!File.Exists(legacyPath) && oldHome != null)
        {
            legacyPath = Path.Combine(oldHome, LegacyFileName);
        }

        _toml = TomlConfig.Load(path);
        if (_toml.LoadError != null || _toml.Errors.Count > 0)
        {
            // The client's own file, so a broken one starts over; otherwise the agreement could
            // never be saved and the free-mod dialog would come back every launch.
            _toml = TomlConfig.FromText("", path);
        }

        if (!File.Exists(legacyPath))
        {
            return;
        }

        bool tomlExisted = File.Exists(path);
        var ini = IniFile.Load(legacyPath);
        if (ini.LoadError != null)
        {
            return;
        }

        // The ini goes only once what it held is on disk. The agreement is carried into a new file
        // only: beside an existing OVSState.toml the ini is a leftover. The install id is carried
        // whenever this file has none, because losing it costs a player without a Steam or Epic
        // id (the Internet Archive build) their account.
        bool carried = false;
        if (!tomlExisted && ini.GetBool("FirstRun", "PaidModWarned", false))
        {
            _toml.Set("FirstRun", "PaidModWarned", SettingKind.Bool, "true");
            carried = true;
        }

        string legacyId = ini.Get("Identity", "InstallId", "").Trim();
        if (IsValidInstallId(legacyId) && !IsValidInstallId(_toml.Get("Identity", "InstallId") ?? ""))
        {
            _toml.Set("Identity", "InstallId", SettingKind.String, legacyId);
            carried = true;
        }

        if (carried && !_toml.Save())
        {
            return;
        }

        try
        {
            File.Delete(legacyPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether the player has agreed to the free-mod notice, which is then not shown again.</summary>
    public bool PaidModWarned { get; private set; }

    /// <summary>
    /// This install's random id (32 hex characters), or "" when there is none yet. The server
    /// resolves a player by it when there is no Steam or Epic id, so it must never change.
    /// </summary>
    public string InstallId { get; private set; } = "";

    /// <summary>Reads the file; returns this instance.</summary>
    public State Load()
    {
        PaidModWarned = ConfigFile.TryParseBool(_toml.Get("FirstRun", "PaidModWarned"), out bool warned) && warned;
        string id = _toml.Get("Identity", "InstallId") ?? "";
        InstallId = IsValidInstallId(id) ? id : "";
        return this;
    }

    /// <summary>Records the agreement and writes the file.</summary>
    public void MarkPaidModWarned()
    {
        PaidModWarned = true;
        _toml.Set("FirstRun", "PaidModWarned", SettingKind.Bool, "true");
        _toml.Save();
    }

    /// <summary>
    /// The stored install id, or a new random one once it is written and read back from disk.
    /// Returns "" when it cannot be stored: an id that is not on disk would be a different one
    /// next launch, and the server would take that for a new install.
    /// </summary>
    public string LoadOrCreateInstallId()
    {
        if (InstallId.Length > 0)
        {
            return InstallId;
        }

        string id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        _toml.Set("Identity", "InstallId", SettingKind.String, id);
        if (!_toml.Save())
        {
            return "";
        }

        string? stored = TomlConfig.Load(_path).Get("Identity", "InstallId");
        if (!string.Equals(stored, id, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        InstallId = id;
        return id;
    }

    /// <summary>Whether <paramref name="value"/> is an install id: exactly 32 hex characters, either case, as the C++ checked.</summary>
    public static bool IsValidInstallId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);
}
