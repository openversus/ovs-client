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

    /// <summary>
    /// The folder of the program a plugin in <paramref name="directory"/> was installed for: the folder that holds its
    /// plugins/ or scripts/ folder (the ASI loader's), or that holds the plugin itself. The mod's own folder is looked
    /// through first, so plugins/OpenVersus/ and a plugin loose in plugins/ both belong to the game beside plugins/.
    /// </summary>
    public static string OwnerFolder(string directory)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (IsHome(folder) && Path.GetDirectoryName(folder) is { } above)
        {
            folder = above;
        }

        string name = Path.GetFileName(folder);
        if ((string.Equals(name, "plugins", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "scripts", StringComparison.OrdinalIgnoreCase))
            && Path.GetDirectoryName(folder) is { } owner)
        {
            folder = owner;
        }

        return folder;
    }

    /// <summary>
    /// Whether the plugin in <paramref name="directory"/> was loaded by <paramref name="hostPath"/>, another program than the
    /// one it was installed for. The game's ASI loader is found by other programs of the install
    /// too: UE's crash reporter (Engine\Binaries\Win64\CrashReportClient.exe, after a crash) and the MultiVersus.exe
    /// launcher at the top of the install. Running there, the plugin moved itself (or retired the game's copy) out of the
    /// game's folder, so the next launch ran without it and the game closed on OVS_P.pak's signature (two players'
    /// logs, 2026-10-31). The game itself is never a guest, wherever its plugin is.
    /// </summary>
    public static bool IsGuest(string directory, string? hostPath) =>
        hostPath != null
        && !string.Equals(Path.GetFileName(hostPath), GameExecutable, StringComparison.OrdinalIgnoreCase)
        && Path.GetDirectoryName(hostPath) is { } hostDirectory
        && !SameFolder(OwnerFolder(directory), hostDirectory);

    /// <summary>The game's executable, on every store's build.</summary>
    public const string GameExecutable = "MultiVersus-Win64-Shipping.exe";

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
