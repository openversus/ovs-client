namespace OpenVersus.Config;

/// <summary>
/// OVSState.toml: what the client has already told this player, and this install's random id.
/// The C++ client's OVSState.ini has its values carried over and is deleted once the TOML is on disk.
/// </summary>
public sealed partial class State
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
        if (_toml.LoadError != null)
        {
            // A file that is there but cannot be read may hold the only copy of the install id,
            // so this launch runs on a document that is never saved over it.
            _toml = TomlConfig.InMemory(path);
        }
        else if (_toml.Errors.Count > 0)
        {
            // The client's own file, so a broken one starts over; otherwise the agreement could
            // never be saved and the free-mod dialog would come back every launch.
            _toml = StartOver(path);
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

    /// <summary>Remembers what an update installed, for a toast on the next launch, and writes the file.</summary>
    public void SetUpdateNotice(string text)
    {
        _toml.Set("Update", "Notice", SettingKind.String, text);
        _toml.Save();
    }

    /// <summary>What the last update installed, or null; cleared once taken, so it shows once.</summary>
    public string? TakeUpdateNotice()
    {
        string? text = _toml.Get("Update", "Notice");
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        _toml.Set("Update", "Notice", SettingKind.String, "");
        _toml.Save();
        return text;
    }

    /// <summary>Whether <paramref name="value"/> is an install id: exactly 32 hex characters, either case, as the C++ checked.</summary>
    public static bool IsValidInstallId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);

    /// <summary>Where a <see cref="FileName"/> that would not parse is kept, beside it, before a new one replaces it.</summary>
    public const string BrokenFileName = FileName + ".broken";

    /// <summary>
    /// A new document for the <see cref="FileName"/> at <paramref name="path"/>, which does not parse.
    /// The broken file is copied to <see cref="BrokenFileName"/> first, and its install id, when a
    /// line of it still reads as one, is written into the new file, since a new id would be a new
    /// account for a player without a Steam or Epic id. When the copy cannot be made, the document
    /// is never saved, so nothing replaces a file that may hold the only copy of the id.
    /// </summary>
    private static TomlConfig StartOver(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return TomlConfig.InMemory(path);
        }

        TomlConfig toml;
        try
        {
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, BrokenFileName), bytes);
            toml = TomlConfig.FromText("", path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            toml = TomlConfig.InMemory(path);
        }

        if (SalvageInstallId(bytes) is { } id)
        {
            toml.Set("Identity", "InstallId", SettingKind.String, id);
            toml.Save();
        }

        return toml;
    }

    /// <summary>
    /// The install id in the text of a state file that does not parse: the one value of every
    /// <c>InstallId = "…"</c> line that holds an id, or null when there is none or they disagree.
    /// Read as Latin-1, so a file that is not UTF-8 still gives up an id written in ASCII.
    /// </summary>
    internal static string? SalvageInstallId(byte[] bytes)
    {
        var ids = InstallIdLine().Matches(System.Text.Encoding.Latin1.GetString(bytes))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*InstallId[ \t]*=[ \t]*[""']?([0-9A-Fa-f]{32})[""']?[ \t]*\r?$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex InstallIdLine();
}
