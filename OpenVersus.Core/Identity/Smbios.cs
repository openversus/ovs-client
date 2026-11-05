namespace OpenVersus.Identity;

/// <summary>
/// What the identity code reads from the SMBIOS table: the system's manufacturer, product name and UUID (type 1) and every
/// baseboard serial (type 2), in table order. Strings are as the firmware wrote them; a missing one is "".
/// </summary>
/// <param name="Manufacturer">Type 1 manufacturer, or "".</param>
/// <param name="Product">Type 1 product name, or "".</param>
/// <param name="SystemUuid">Type 1 UUID as lowercase 8-4-4-4-12 text, or "" when the structure has none.</param>
/// <param name="BoardSerials">Every type 2 serial, in table order; "" for a baseboard without one.</param>
public sealed record SmbiosInfo(string Manufacturer, string Product, string SystemUuid, IReadOnlyList<string> BoardSerials)
{
    public static readonly SmbiosInfo Empty = new("", "", "", []);
}

/// <summary>The raw SMBIOS table (GetSystemFirmwareTable 'RSMB'), parsed as far as the identity code needs.</summary>
public static class Smbios
{
    /// <summary>The table through <paramref name="read"/>, parsed; null when there is none or reading it throws.</summary>
    public static SmbiosInfo? TryRead(Func<byte[]?> read)
    {
        try
        {
            byte[]? raw = read();
            return raw == null ? null : Parse(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the raw table: an 8-byte header (calling method, version, DMI revision, table length) and then the
    /// structures, each a formatted area followed by its string set, ended by a double NUL; type 127 ends the table.
    /// </summary>
    public static SmbiosInfo Parse(byte[] raw)
    {
        string manufacturer = "", product = "", uuid = "";
        bool systemSeen = false;
        var serials = new List<string>();
        if (raw.Length < 8)
        {
            return SmbiosInfo.Empty;
        }

        int tableLength = BitConverter.ToInt32(raw, 4);
        int end = tableLength < 0 ? raw.Length : Math.Min(raw.Length, 8 + tableLength);
        int pos = 8;
        while (pos + 4 <= end)
        {
            byte type = raw[pos], length = raw[pos + 1];
            if (length < 4 || pos + length > end || type == 127)
            {
                break;
            }

            int strings = pos + length;
            if (type == 1 && length >= 8 && !systemSeen)
            {
                systemSeen = true;
                manufacturer = StringAt(raw, strings, end, raw[pos + 4]);
                product = StringAt(raw, strings, end, raw[pos + 5]);
                if (length >= 0x18)
                {
                    uuid = FormatUuid(raw.AsSpan(pos + 8, 16));
                }
            }
            else if (type == 2 && length >= 8)
            {
                serials.Add(StringAt(raw, strings, end, raw[pos + 7]));
            }

            int q = strings;
            while (q + 1 < end && !(raw[q] == 0 && raw[q + 1] == 0))
            {
                q++;
            }

            pos = q + 2;
        }

        return new SmbiosInfo(manufacturer, product, uuid, serials);
    }

    /// <summary>
    /// The UUID as Windows reports it (wmic csproduct get uuid): the first three fields are little-endian in the table
    /// (SMBIOS 2.6 and later; every machine the game runs on), the rest in order. All zero (not present) or all 0xFF
    /// (not settable) give "".
    /// </summary>
    internal static string FormatUuid(ReadOnlySpan<byte> b)
    {
        bool allZero = true, allOnes = true;
        foreach (byte value in b)
        {
            allZero &= value == 0;
            allOnes &= value == 0xFF;
        }

        if (allZero || allOnes)
        {
            return "";
        }

        return $"{b[3]:x2}{b[2]:x2}{b[1]:x2}{b[0]:x2}-{b[5]:x2}{b[4]:x2}-{b[7]:x2}{b[6]:x2}-{b[8]:x2}{b[9]:x2}-{b[10]:x2}{b[11]:x2}{b[12]:x2}{b[13]:x2}{b[14]:x2}{b[15]:x2}";
    }

    /// <summary>String number <paramref name="index"/> (1-based; 0 means none) of the set starting at <paramref name="at"/>, or "".</summary>
    private static string StringAt(byte[] raw, int at, int end, byte index)
    {
        if (index == 0)
        {
            return "";
        }

        int s = at;
        for (int i = 1; i < index && s < end; i++)
        {
            s += StrLen(raw, s, end) + 1;
        }

        return s < end && raw[s] != 0 ? System.Text.Encoding.UTF8.GetString(raw, s, StrLen(raw, s, end)) : "";
    }

    /// <summary>Length of the NUL-terminated string at <paramref name="at"/>, or to <paramref name="end"/> if it never ends.</summary>
    private static int StrLen(byte[] buffer, int at, int end)
    {
        int nul = buffer.AsSpan(at, end - at).IndexOf((byte)0);
        return nul < 0 ? end - at : nul;
    }
}
