using OpenVersus.Config;
using OpenVersus.Memory;

namespace OpenVersus.Game;

/// <summary>Where one named pattern from the ini was found, or that it was not.</summary>
public readonly record struct PatternHit(string Name, string Text, nint Address, bool FromCache)
{
    public bool Found => Address != 0;
    public static PatternHit Missing(string name, string text) => new(name, text, 0, false);
    public static implicit operator nint(PatternHit hit) => hit.Address;
}

/// <summary>
/// Finds the ini's patterns in the image, through the cache first. Same semantics as the C++
/// PatternFinder: first match over the whole image, and a cached address is used when present,
/// except that here the pattern is checked to still match at the cached address.
/// </summary>
public sealed class PatternResolver(GameImage image, PatternCache cache, Settings settings, Log log)
{
    public GameImage Image { get; } = image;

    /// <summary>Finds the pattern the ini keeps under <paramref name="name"/>.</summary>
    public PatternHit Find(string name) => Find(name, settings.Pattern(name));

    public PatternHit Find(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            log.Error($"{name}: no pattern in the ini");
            return PatternHit.Missing(name, text);
        }
        BytePattern pattern;
        try
        {
            pattern = BytePattern.Parse(text);
        }
        catch (FormatException e)
        {
            log.Error($"{name}: bad pattern ({e.Message})");
            return PatternHit.Missing(name, text);
        }

        uint cached = cache.Load(text);
        if (cached != 0 && cached < (uint)Image.Size && pattern.MatchesAt(Image.Bytes, (int)cached))
        {
            nint at = Image.Address(cached);
            log.Debug($"{name} pattern cached at 0x{at:X} (rva 0x{cached:X})");
            return new PatternHit(name, text, at, true);
        }
        if (cached != 0)
        {
            log.Warn($"{name}: cached rva 0x{cached:X} no longer matches; rescanning");
        }

        int offset = PatternScanner.FindFirst(Image.Bytes, pattern);
        if (offset < 0)
        {
            log.Error($"{name}: pattern not found");
            cache.Save(text, 0);
            return PatternHit.Missing(name, text);
        }
        nint found = Image.Address((uint)offset);
        cache.Save(text, (uint)offset);
        log.Debug($"{name} pattern found at 0x{found:X} (rva 0x{offset:X})");
        return new PatternHit(name, text, found, false);
    }
}
