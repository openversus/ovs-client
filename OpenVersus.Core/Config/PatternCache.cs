using System.Globalization;

namespace OpenVersus.Config;

/// <summary>
/// PatternsCache.toml: pattern text to RVA for one game executable and client version, which
/// the file names at its top (<c>Exe</c>, the top 32 bits of the .text hash, and <c>Client</c>). A
/// file for another exe or client, or one that does not parse, is started over. Each result is
/// written as soon as it is known, and a cached RVA is only trusted if the pattern still matches
/// there; the C++ took it on faith. The C++ client's PatternsCache.cache is deleted: it is only a
/// cache.
/// </summary>
public sealed class PatternCache
{
    /// <summary>The cache file's name, beside the plugin.</summary>
    public const string FileName = "PatternsCache.toml";

    /// <summary>The cache file the C++ client and earlier versions of this one used.</summary>
    public const string LegacyFileName = "PatternsCache.cache";

    private const string Patterns = "Patterns";
    private readonly TomlConfig _toml;

    /// <summary>
    /// Opens the cache in <paramref name="directory"/> for <paramref name="textHash"/> and
    /// <paramref name="version"/>. A hash of 0 (no .text section found) turns the cache off. Old
    /// caches here and in <paramref name="oldHome"/> (<see cref="Layout.OldHome"/>) are deleted.
    /// </summary>
    public PatternCache(string directory, ulong textHash, string version, string? oldHome = null)
    {
        string path = Path.Combine(directory, FileName);
        // Old caches, here or loose in plugins/ from an earlier layout, are only caches.
        string?[] stale = [Path.Combine(directory, LegacyFileName),
            oldHome == null ? null : Path.Combine(oldHome, LegacyFileName),
            oldHome == null ? null : Path.Combine(oldHome, FileName)];
        foreach (string? file in stale)
        {
            try
            {
                if (file != null)
                {
                    File.Delete(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        string exe = ((uint)(textHash >> 32)).ToString("X8", CultureInfo.InvariantCulture);
        Section = textHash == 0 ? null : $"{exe}.{version}";
        _toml = TomlConfig.Load(path);
        if (Section != null && (_toml.Get("", "Exe") != exe || _toml.Get("", "Client") != version))
        {
            _toml = TomlConfig.FromText("", path);
            _toml.Set("", "Exe", SettingKind.String, exe);
            _toml.Set("", "Client", SettingKind.String, version);
        }
    }

    /// <summary>The exe and client version this cache is for, or null when the cache is off.</summary>
    public string? Section { get; }

    /// <summary>The cached RVA, or 0 when unknown (a miss was cached as 0 too, as the C++ did).</summary>
    public uint Load(string pattern) =>
        Section != null && uint.TryParse(_toml.Get(Patterns, pattern), NumberStyles.None, CultureInfo.InvariantCulture, out uint rva) ? rva : 0;

    /// <summary>Records <paramref name="rva"/> for <paramref name="pattern"/>, 0 for a miss, and writes the file at once.</summary>
    public void Save(string pattern, uint rva)
    {
        if (Section != null)
        {
            _toml.Set(Patterns, pattern, SettingKind.Int, rva.ToString(CultureInfo.InvariantCulture));
            _toml.Save();
        }
    }
}
