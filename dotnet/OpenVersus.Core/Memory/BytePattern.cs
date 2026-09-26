namespace OpenVersus.Memory;

/// <summary>
/// A byte pattern in the form the C++ client's [Patterns] entries use, parsed with the same
/// rules as its TransformPattern: spaces are ignored, every single '?' is one wildcard byte
/// (so "??" is two), hex digits pair up into bytes, and any other character is skipped. Patterns
/// carried over from players' OpenVersus.ini files must mean the same thing here as they did there.
/// </summary>
public sealed class BytePattern
{
    /// <summary>The bytes to match; 0 at a wildcard.</summary>
    public byte[] Bytes { get; }
    /// <summary>0xFF where the byte must equal <see cref="Bytes"/>, 0 for a wildcard, as in the C++.</summary>
    public byte[] Mask { get; }
    /// <summary>The pattern as given, which is also its key in the pattern cache.</summary>
    public string Text { get; }
    /// <summary>The longest run of fixed bytes; the scanner searches for it first.</summary>
    public int AnchorStart { get; }
    /// <summary>The length of the run at <see cref="AnchorStart"/>.</summary>
    public int AnchorLength { get; }

    /// <summary>The pattern's length in bytes, wildcards included.</summary>
    public int Length => Bytes.Length;

    private BytePattern(byte[] bytes, byte[] mask, string text)
    {
        Bytes = bytes;
        Mask = mask;
        Text = text;
        for (int i = 0; i < bytes.Length;)
        {
            if (mask[i] == 0)
            {
                i++;
                continue;
            }
            int start = i;
            while (i < bytes.Length && mask[i] != 0)
            {
                i++;
            }

            if (i - start > AnchorLength)
            {
                (AnchorStart, AnchorLength) = (start, i - start);
            }
        }
    }

    /// <summary>Parses <paramref name="text"/>; throws a <see cref="FormatException"/> when it is empty or has no fixed bytes.</summary>
    public static BytePattern Parse(string text)
    {
        var bytes = new List<byte>();
        var mask = new List<byte>();
        byte pending = 0;
        bool half = false;
        foreach (char ch in text)
        {
            if (ch == ' ')
            {
                continue;
            }

            if (ch == '?')
            {
                bytes.Add(0);
                mask.Add(0);
            }
            else if (HexValue(ch) is int digit)
            {
                if (!half)
                {
                    pending = (byte)(digit << 4);
                    half = true;
                }
                else
                {
                    pending |= (byte)digit;
                    half = false;
                    bytes.Add(pending);
                    mask.Add(0xFF);
                }
            }
            // Anything else is ignored, exactly as the C++ ignores it.
        }
        if (bytes.Count == 0)
        {
            throw new FormatException("pattern is empty");
        }

        if (!mask.Contains((byte)0xFF))
        {
            throw new FormatException($"pattern \"{text}\" has no fixed bytes");
        }

        return new BytePattern(bytes.ToArray(), mask.ToArray(), text);
    }

    private static int? HexValue(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'A' and <= 'F' => ch - 'A' + 10,
        >= 'a' and <= 'f' => ch - 'a' + 10,
        _ => null,
    };

    /// <summary>Whether the pattern matches <paramref name="haystack"/> at offset <paramref name="at"/>; false when it would run past either end.</summary>
    public bool MatchesAt(ReadOnlySpan<byte> haystack, int at)
    {
        if (at < 0 || at + Bytes.Length > haystack.Length)
        {
            return false;
        }

        for (int i = 0; i < Bytes.Length; i++)
        {
            if ((haystack[at + i] & Mask[i]) != Bytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The pattern as given.</summary>
    public override string ToString() => Text;
}
