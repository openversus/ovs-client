namespace OpenVersus.Config;

/// <summary>
/// OVSState.toml: what the client has already told this player. The C++ client's OVSState.ini,
/// when it is all there is, has its one value carried over and is deleted once the TOML is on disk.
/// </summary>
public sealed class State
{
    /// <summary>The state file's name, beside the plugin.</summary>
    public const string FileName = "OVSState.toml";

    /// <summary>The state file the C++ client and earlier versions of this one used.</summary>
    public const string LegacyFileName = "OVSState.ini";

    private readonly TomlConfig _toml;

    /// <summary>Opens <see cref="FileName"/> in <paramref name="directory"/>, converting a legacy
    /// <see cref="LegacyFileName"/> first when that is all there is.</summary>
    public State(string directory)
    {
        string path = Path.Combine(directory, FileName);
        string legacyPath = Path.Combine(directory, LegacyFileName);
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

        var ini = File.Exists(path) ? null : IniFile.Load(legacyPath);
        if (ini?.LoadError != null)
        {
            return;
        }

        // The ini goes only once what it held is on disk; a player who never agreed has nothing to
        // carry, and beside an existing OVSState.toml it is only a leftover.
        if (ini != null && ini.GetBool("FirstRun", "PaidModWarned", false))
        {
            _toml.Set("FirstRun", "PaidModWarned", SettingKind.Bool, "true");
            if (!_toml.Save())
            {
                return;
            }
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

    /// <summary>Reads the file; returns this instance.</summary>
    public State Load()
    {
        PaidModWarned = IniFile.TryParseBool(_toml.Get("FirstRun", "PaidModWarned"), out bool warned) && warned;
        return this;
    }

    /// <summary>Records the agreement and writes the file.</summary>
    public void MarkPaidModWarned()
    {
        PaidModWarned = true;
        _toml.Set("FirstRun", "PaidModWarned", SettingKind.Bool, "true");
        _toml.Save();
    }
}
