namespace OpenVersus.Memory;

public static class PatternScanner
{
    /// <summary>
    /// The first offset where the pattern matches, or -1. This is what the C++ FindPattern did
    /// over the whole image: first match wins and nothing checks for a second one. Some patterns
    /// in the shipped ini rely on that (Notifications matches three identical functions).
    /// </summary>
    public static int FindFirst(ReadOnlySpan<byte> haystack, BytePattern pattern)
    {
        ReadOnlySpan<byte> anchor = pattern.Bytes.AsSpan(pattern.AnchorStart, pattern.AnchorLength);
        int from = pattern.AnchorStart;
        while (from < haystack.Length)
        {
            int i = haystack[from..].IndexOf(anchor);
            if (i < 0)
            {
                return -1;
            }

            int at = from + i - pattern.AnchorStart;
            if (pattern.MatchesAt(haystack, at))
            {
                return at;
            }

            from += i + 1;
        }
        return -1;
    }

    /// <summary>Every offset where the pattern matches, overlapping matches included.</summary>
    public static List<int> FindAll(ReadOnlySpan<byte> haystack, BytePattern pattern)
    {
        var hits = new List<int>();
        ReadOnlySpan<byte> anchor = pattern.Bytes.AsSpan(pattern.AnchorStart, pattern.AnchorLength);
        int from = pattern.AnchorStart;
        while (from < haystack.Length)
        {
            int i = haystack[from..].IndexOf(anchor);
            if (i < 0)
            {
                break;
            }

            int at = from + i - pattern.AnchorStart;
            if (pattern.MatchesAt(haystack, at))
            {
                hits.Add(at);
            }

            from += i + 1;
        }
        return hits;
    }
}
