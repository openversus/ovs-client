using OpenVersus.Config;

namespace OpenVersus;

/// <summary>
/// What Ultimate ASI Loader will load, read from its settings the way it reads them
/// (source/dllmain.cpp, Init and LoadPlugins). It lives beside the game executable and reads
/// [GlobalSets] from, in order, each file overriding the ones before: &lt;its own name&gt;.ini,
/// global.ini, scripts\global.ini, plugins\global.ini and update\global.ini, all beside it.
/// Folders under plugins/ are loaded when LoadPlugins and LoadRecursively are both on, which is
/// the default when no file says otherwise. LoadFromScriptsOnly only stops .asi files in the
/// game's own folder, so it does not matter here.
/// </summary>
public static class LoaderConfig
{
    private const string Section = "GlobalSets";

    /// <summary>
    /// Whether the loader beside <paramref name="gameDirectory"/> loads .asi files in folders
    /// under plugins/, such as plugins/OpenVersus/. <paramref name="reason"/> names the setting
    /// and file that turned it off, or says the defaults apply.
    /// </summary>
    public static bool LoadsPluginSubfolders(string gameDirectory, out string reason)
    {
        bool loadPlugins = true;
        bool recursive = true;
        string? loadPluginsFrom = null;
        string? recursiveFrom = null;
        foreach (string file in SettingsFiles(gameDirectory))
        {
            var ini = IniFile.Load(file);
            if (ini.LoadError != null)
            {
                continue;
            }

            if (ProfileInt(ini.Get(Section, "LoadPlugins")) is int plugins)
            {
                loadPlugins = plugins != 0;
                loadPluginsFrom = file;
            }

            if (ProfileInt(ini.Get(Section, "LoadRecursively")) is int deep)
            {
                recursive = deep != 0;
                recursiveFrom = file;
            }
        }

        reason = !loadPlugins ? $"LoadPlugins is off in {loadPluginsFrom}"
            : !recursive ? $"LoadRecursively is off in {recursiveFrom}"
            : loadPluginsFrom == null && recursiveFrom == null ? "no loader settings, so its defaults apply"
            : "the loader's settings leave LoadPlugins and LoadRecursively on";
        return loadPlugins && recursive;
    }

    /// <summary>
    /// The loader's settings files beside <paramref name="gameDirectory"/>, in the order it reads
    /// them. Its own .ini is named after it, and it may be xinput1_3.dll, version.dll or another
    /// proxy name, so the .ini of every DLL there comes first.
    /// </summary>
    private static IEnumerable<string> SettingsFiles(string gameDirectory)
    {
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
        IEnumerable<string> dlls;
        try
        {
            dlls = Directory.EnumerateFiles(gameDirectory, "*.dll", options).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            dlls = [];
        }

        foreach (string dll in dlls)
        {
            string ini = Path.ChangeExtension(dll, ".ini");
            if (File.Exists(ini))
            {
                yield return ini;
            }
        }

        foreach (string relative in new[] { "global.ini", "scripts/global.ini", "plugins/global.ini", "update/global.ini" })
        {
            string file = Path.Combine(gameDirectory, relative);
            if (File.Exists(file))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// A value as GetPrivateProfileInt reads it: null when the key is missing, otherwise its
    /// leading decimal number, 0 when it has none.
    /// </summary>
    private static int? ProfileInt(string? value)
    {
        if (value == null)
        {
            return null;
        }

        string text = value.Trim();
        int length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        return length == 0 ? 0 : int.TryParse(text.AsSpan(0, Math.Min(length, 9)), out int n) ? n : 0;
    }
}
