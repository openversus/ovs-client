namespace OpenVersus;

/// <summary>
/// Where the mod lives: its own folder inside the ASI loader's plugins folder,
/// plugins/OpenVersus/, holding the plugin and everything it writes (settings, state, cache, logs).
/// Earlier versions lived loose in plugins/, so a plugin running from its own folder takes over
/// the old files it finds one folder up (<see cref="OldHome"/>); a logs folder is never among them,
/// since the C++ client wrote none and plugins/logs may belong to another mod.
/// </summary>
public static class Layout
{
    /// <summary>The mod's folder name inside plugins/.</summary>
    public const string FolderName = "OpenVersus";

    /// <summary>Whether <paramref name="directory"/> is the mod's own folder.</summary>
    public static bool IsHome(string directory) =>
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), FolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The mod's folder for a plugin in <paramref name="directory"/>: that folder when it already is
    /// the mod's folder, else the mod's folder inside it (a plugin still loose in plugins/).
    /// </summary>
    public static string HomeFor(string directory) => IsHome(directory) ? directory : Path.Combine(directory, FolderName);

    /// <summary>Where the old layout's files are for a plugin in <paramref name="directory"/>: the
    /// folder above, when it runs from the mod's folder; null otherwise.</summary>
    public static string? OldHome(string directory) =>
        IsHome(directory) ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) : null;

    /// <summary>The files the mod keeps beside itself, current and legacy. Logs are not among them:
    /// a logs folder outside the mod's own folder may be shared with other mods.</summary>
    public static readonly IReadOnlyList<string> ModFiles =
    [
        Config.Settings.FileName, Config.Settings.LegacyFileName,
        Config.State.FileName, Config.State.LegacyFileName,
        Config.PatternCache.FileName, Config.PatternCache.LegacyFileName,
    ];

    /// <summary>Whether two folder paths name the same folder, regardless of case and trailing separators.</summary>
    public static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves the running plugin at <paramref name="pluginPath"/> into <paramref name="home"/>, then
    /// the mod's files beside it (<see cref="ModFiles"/>) that <paramref name="home"/> lacks. Windows
    /// allows moving a loaded module within a drive. Returns the plugin's new path, or null when it
    /// could not be moved, in which case nothing was. <paramref name="notes"/> says what happened.
    /// </summary>
    public static string? MoveInto(string pluginPath, string home, out List<string> notes)
    {
        notes = [];
        string from = Path.GetDirectoryName(pluginPath)!;
        string to = Path.Combine(home, Path.GetFileName(pluginPath));
        try
        {
            Directory.CreateDirectory(home);
            // Never over an existing file: a copy already there is the duplicate check's business.
            File.Move(pluginPath, to, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add($"could not move {pluginPath} to {home}: {e.Message}");
            return null;
        }

        notes.Add($"moved {pluginPath} to {to}");
        foreach (string name in ModFiles)
        {
            if (TakeOver(from, home, name) is { } note)
            {
                notes.Add(note);
            }
        }

        return to;
    }

    /// <summary>
    /// Moves <paramref name="name"/> from <paramref name="oldHome"/> into <paramref name="home"/>
    /// when it is there and <paramref name="home"/> has none. Returns what happened, for the log, or
    /// null when there was nothing to move.
    /// </summary>
    public static string? TakeOver(string? oldHome, string home, string name)
    {
        if (oldHome == null)
        {
            return null;
        }

        string from = Path.Combine(oldHome, name);
        string to = Path.Combine(home, name);
        if (!File.Exists(from))
        {
            return null;
        }

        if (File.Exists(to))
        {
            return $"{from} is left as it is because {to} exists";
        }

        try
        {
            File.Move(from, to);
            return $"moved {from} to {to}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"could not move {from} to {to}: {e.Message}";
        }
    }
}
