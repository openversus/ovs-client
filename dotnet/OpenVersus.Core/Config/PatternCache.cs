using System.Globalization;

namespace OpenVersus.Config;

/// <summary>
/// PatternsCache.cache: pattern text to RVA, under a section named by the top 32 bits of the
/// .text hash and the client version, so a different exe or client starts a fresh section. The
/// format is the C++ client's and each result is written as soon as it is known. A cached RVA is only trusted if the pattern still matches there;
/// the C++ took it on faith.
/// </summary>
public sealed class PatternCache
{
    public const string FileName = "PatternsCache.cache";
    private readonly IniFile _ini;
    private readonly string? _section;

    public PatternCache(string path, ulong textHash, string version)
    {
        _ini = IniFile.Load(path);
        _section = textHash == 0 ? null : $"{(uint)(textHash >> 32):X8}.{version}";
    }

    public string? Section => _section;

    /// <summary>The cached RVA, or 0 when unknown (a miss was cached as 0 too, as the C++ did).</summary>
    public uint Load(string pattern) => _section == null ? 0 : (uint)_ini.GetUInt64(_section, pattern, 0);

    public void Save(string pattern, uint rva)
    {
        if (_section != null)
        {
            _ini.Set(_section, pattern, rva.ToString(CultureInfo.InvariantCulture));
            _ini.Save();
        }
    }
}
