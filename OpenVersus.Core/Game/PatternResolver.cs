using System.Buffers.Binary;
using System.Text;
using OpenVersus.Config;
using OpenVersus.Memory;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Game;

/// <summary>Where one named pattern from the ini was found, or that it was not.</summary>
/// <param name="Name">The ini key the pattern was found under.</param>
/// <param name="Text">The pattern as written in the ini.</param>
/// <param name="Address">Where it matched; 0 when it did not.</param>
/// <param name="FromCache">Whether the address came from the pattern cache rather than a scan.</param>
public readonly record struct PatternHit(string Name, string Text, nint Address, bool FromCache)
{
    /// <summary>True when an address was found.</summary>
    public bool Found => Address != 0;
    /// <summary>A hit for a pattern that is blank, does not parse, or matched nothing.</summary>
    public static PatternHit Missing(string name, string text) => new(name, text, 0, false);
    /// <summary>The address, or 0 when the pattern was not found.</summary>
    public static implicit operator nint(PatternHit hit) => hit.Address;
}

/// <summary>
/// Finds the ini's patterns in the image, through the cache first. Same semantics as the C++
/// PatternFinder: first match over the whole image, and a cached address is used when present,
/// except that here the pattern is checked to still match at the cached address.
/// </summary>
public sealed class PatternResolver(GameImage image, PatternCache cache, Settings settings, ILogger log)
{
    /// <summary>The image the patterns are searched in.</summary>
    public GameImage Image { get; } = image;

    /// <summary>Finds the pattern the ini keeps under <paramref name="name"/>.</summary>
    public PatternHit Find(string name) => Find(name, settings.Pattern(name));

    /// <summary>
    /// Finds <paramref name="text"/>, logging under <paramref name="name"/>: the cached RVA if the pattern
    /// still matches there, otherwise the first match in the image, which is then cached. A blank or
    /// unparsable pattern, or no match at all, is logged as an error and returns a missing hit.
    /// </summary>
    public PatternHit Find(string name, string text)
    {
        if (!TryParse(name, text, out var pattern))
        {
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

    /// <summary>
    /// Finds, among every match of <paramref name="text"/>, the one whose RIP-relative instruction at
    /// <paramref name="operandOffset"/> (seven bytes, the disp32 last: <c>lea r64, [rip + disp32]</c>)
    /// points at <paramref name="utf16"/> as a NUL-terminated UTF-16 string. For a function that differs
    /// from its siblings only in the string it names, such as the Store's two event recorders. Cached
    /// under the pattern and the string together, and the cached address is checked the same way.
    /// </summary>
    public PatternHit FindNaming(string name, string text, int operandOffset, string utf16)
    {
        if (!TryParse(name, text, out var pattern))
        {
            return PatternHit.Missing(name, text);
        }

        string key = $"{text} -> {utf16}";
        uint cached = cache.Load(key);
        if (cached != 0 && cached < (uint)Image.Size && Names(Image.Bytes, pattern, (int)cached, operandOffset, utf16))
        {
            nint at = Image.Address(cached);
            log.Debug($"{name} pattern naming {utf16} cached at 0x{at:X} (rva 0x{cached:X})");
            return new PatternHit(name, text, at, true);
        }
        if (cached != 0)
        {
            log.Warn($"{name}: cached rva 0x{cached:X} no longer matches or names {utf16}; rescanning");
        }

        int offset = FindNaming(Image.Bytes, pattern, operandOffset, utf16);
        if (offset < 0)
        {
            log.Error($"{name}: no match of the pattern names {utf16}");
            cache.Save(key, 0);
            return PatternHit.Missing(name, text);
        }
        nint found = Image.Address((uint)offset);
        cache.Save(key, (uint)offset);
        log.Debug($"{name} pattern naming {utf16} found at 0x{found:X} (rva 0x{offset:X})");
        return new PatternHit(name, text, found, false);
    }

    /// <summary>The first match of <paramref name="pattern"/> in <paramref name="image"/> that <see cref="Names"/> <paramref name="utf16"/>, or -1.</summary>
    internal static int FindNaming(ReadOnlySpan<byte> image, BytePattern pattern, int operandOffset, string utf16)
    {
        foreach (int at in PatternScanner.FindAll(image, pattern))
        {
            if (Names(image, pattern, at, operandOffset, utf16))
            {
                return at;
            }
        }
        return -1;
    }

    /// <summary>
    /// Whether <paramref name="pattern"/> matches <paramref name="image"/> at <paramref name="at"/> and the
    /// RIP-relative operand <paramref name="operandOffset"/> bytes in points at <paramref name="utf16"/>,
    /// NUL-terminated, inside the image.
    /// </summary>
    internal static bool Names(ReadOnlySpan<byte> image, BytePattern pattern, int at, int operandOffset, string utf16)
    {
        if (!pattern.MatchesAt(image, at))
        {
            return false;
        }

        int next = at + operandOffset + 7;
        if (next > image.Length)
        {
            return false;
        }

        long target = next + BinaryPrimitives.ReadInt32LittleEndian(image[(next - 4)..]);
        byte[] expected = Encoding.Unicode.GetBytes(utf16 + "\0");
        return target >= 0 && target + expected.Length <= image.Length && image.Slice((int)target, expected.Length).SequenceEqual(expected);
    }

    private bool TryParse(string name, string text, out BytePattern pattern)
    {
        pattern = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            log.Error($"{name}: no pattern in the ini");
            return false;
        }
        try
        {
            pattern = BytePattern.Parse(text);
            return true;
        }
        catch (FormatException e)
        {
            log.Error($"{name}: bad pattern ({e.Message})");
            return false;
        }
    }
}
