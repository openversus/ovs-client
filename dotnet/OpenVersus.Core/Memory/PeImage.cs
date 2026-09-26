namespace OpenVersus.Memory;

/// <summary>One entry of a PE section table.</summary>
/// <param name="Name">The section name, such as .text, without its padding.</param>
/// <param name="Rva">Where the section is mapped, relative to the image base.</param>
/// <param name="VirtualSize">Its size when mapped.</param>
/// <param name="RawOffset">Where its data starts in the file.</param>
/// <param name="RawSize">The size of its data in the file.</param>
/// <param name="Characteristics">The IMAGE_SCN_* flags.</param>
public readonly record struct PeSection(string Name, uint Rva, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics);

/// <summary>Reads what this needs out of a PE image: the section table and the mapped size.</summary>
public static unsafe class PeImage
{
    private const uint IMAGE_SCN_CNT_INITIALIZED_DATA = 0x00000040;
    private const uint IMAGE_SCN_MEM_EXECUTE = 0x20000000;
    private const uint IMAGE_SCN_MEM_READ = 0x40000000;
    private const uint IMAGE_SCN_MEM_WRITE = 0x80000000;

    /// <summary>The PE headers of a module mapped in this process, as a span.</summary>
    public static ReadOnlySpan<byte> HeadersInMemory(byte* image)
    {
        int lfanew = *(int*)(image + 0x3C);
        int sectionCount = *(ushort*)(image + lfanew + 6);
        int optionalHeaderSize = *(ushort*)(image + lfanew + 20);
        return new ReadOnlySpan<byte>(image, lfanew + 24 + optionalHeaderSize + sectionCount * 40);
    }

    /// <summary>The whole mapped image, base to SizeOfImage, which is the range the pattern scan covers.</summary>
    public static ReadOnlySpan<byte> ImageInMemory(byte* image) =>
        new(image, checked((int)SizeOfImage(HeadersInMemory(image))));

    private static int NtHeaders(ReadOnlySpan<byte> headers)
    {
        if (headers.Length < 0x40 || headers[0] != 'M' || headers[1] != 'Z')
        {
            throw new FormatException("not a PE image (no MZ)");
        }

        int lfanew = BitConverter.ToInt32(headers[0x3C..]);
        if (BitConverter.ToUInt32(headers[lfanew..]) != 0x00004550)
        {
            throw new FormatException("not a PE image (no PE signature)");
        }

        return lfanew;
    }

    /// <summary>SizeOfImage from the optional header: the mapped image's size. Throws a <see cref="FormatException"/> when <paramref name="headers"/> are not a PE's.</summary>
    public static uint SizeOfImage(ReadOnlySpan<byte> headers)
    {
        int lfanew = NtHeaders(headers);
        return BitConverter.ToUInt32(headers[(lfanew + 24 + 56)..]);
    }

    /// <summary>
    /// Data directory <paramref name="index"/> (2 is resources) as (RVA, size), from a PE32 or PE32+
    /// optional header; (0, 0) when the image has fewer directories. Throws a
    /// <see cref="FormatException"/> when <paramref name="headers"/> are not a PE's.
    /// </summary>
    public static (uint Rva, uint Size) DataDirectory(ReadOnlySpan<byte> headers, int index)
    {
        int optional = NtHeaders(headers) + 24;
        bool pe32Plus = BitConverter.ToUInt16(headers[optional..]) == 0x20B;
        uint count = BitConverter.ToUInt32(headers[(optional + (pe32Plus ? 108 : 92))..]);
        if (index >= count)
        {
            return (0, 0);
        }

        int entry = optional + (pe32Plus ? 112 : 96) + (index * 8);
        return (BitConverter.ToUInt32(headers[entry..]), BitConverter.ToUInt32(headers[(entry + 4)..]));
    }

    /// <summary>Where <paramref name="rva"/> lies in the file, or -1 when no section holds it.</summary>
    public static long FileOffset(IReadOnlyList<PeSection> sections, uint rva)
    {
        foreach (var s in sections)
        {
            if (rva >= s.Rva && rva < s.Rva + Math.Max(s.VirtualSize, s.RawSize))
            {
                return s.RawOffset + (long)(rva - s.Rva);
            }
        }

        return -1;
    }

    /// <summary>The section table. Throws a <see cref="FormatException"/> when <paramref name="headers"/> are not a PE's.</summary>
    public static List<PeSection> Sections(ReadOnlySpan<byte> headers)
    {
        int lfanew = NtHeaders(headers);
        int sectionCount = BitConverter.ToUInt16(headers[(lfanew + 6)..]);
        int optionalHeaderSize = BitConverter.ToUInt16(headers[(lfanew + 20)..]);
        var table = headers[(lfanew + 24 + optionalHeaderSize)..];

        var sections = new List<PeSection>(sectionCount);
        for (int i = 0; i < sectionCount; i++)
        {
            var s = table.Slice(i * 40, 40);
            sections.Add(new PeSection(
                Name: System.Text.Encoding.ASCII.GetString(s[..8]).TrimEnd('\0'),
                VirtualSize: BitConverter.ToUInt32(s[8..]),
                Rva: BitConverter.ToUInt32(s[12..]),
                RawSize: BitConverter.ToUInt32(s[16..]),
                RawOffset: BitConverter.ToUInt32(s[20..]),
                Characteristics: BitConverter.ToUInt32(s[36..])));
        }
        return sections;
    }

    /// <summary>
    /// The function containing <paramref name="rva"/> as (begin, end) RVAs from the exception
    /// directory (.pdata), following chained unwind info from a split-off chunk to the function
    /// it belongs to, as the reverse-engineering toolkit's Image.function_at does. Null when no
    /// entry covers it. <paramref name="image"/> is the mapped image.
    /// </summary>
    public static (uint Begin, uint End)? FunctionContaining(ReadOnlySpan<byte> image, uint rva)
    {
        int lfanew = NtHeaders(image);
        int dir = lfanew + 24 + 0x70 + 3 * 8; // exception directory
        uint pdata = BitConverter.ToUInt32(image[dir..]);
        uint size = BitConverter.ToUInt32(image[(dir + 4)..]);
        if (pdata == 0 || size < 12 || pdata + size > (uint)image.Length)
        {
            return null;
        }

        int count = (int)(size / 12);
        int lo = 0, hi = count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            uint begin = BitConverter.ToUInt32(image[(int)(pdata + mid * 12)..]);
            uint end = BitConverter.ToUInt32(image[(int)(pdata + mid * 12 + 4)..]);
            if (rva < begin)
            {
                hi = mid - 1;
            }
            else if (rva >= end)
            {
                lo = mid + 1;
            }
            else
            {
                found = mid;
                break;
            }
        }
        if (found < 0)
        {
            return null;
        }

        for (int guard = 0; guard < 16; guard++)
        {
            int entry = (int)(pdata + found * 12);
            uint unwind = BitConverter.ToUInt32(image[(entry + 8)..]) & ~1u;
            if (unwind == 0 || unwind + 4 > (uint)image.Length || ((image[(int)unwind] >> 3) & 0x4) == 0)
            {
                break;
            }

            int codes = image[(int)unwind + 2];
            int chained = (int)unwind + 4 + 2 * (codes + (codes & 1));
            uint parentBegin = BitConverter.ToUInt32(image[chained..]);
            int parent = -1;
            lo = 0;
            hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                uint begin = BitConverter.ToUInt32(image[(int)(pdata + mid * 12)..]);
                if (begin < parentBegin)
                {
                    lo = mid + 1;
                }
                else if (begin > parentBegin)
                {
                    hi = mid - 1;
                }
                else
                {
                    parent = mid;
                    break;
                }
            }
            if (parent < 0)
            {
                break;
            }

            found = parent;
        }
        int e = (int)(pdata + found * 12);
        return (BitConverter.ToUInt32(image[e..]), BitConverter.ToUInt32(image[(e + 4)..]));
    }

    /// <summary>A PE file laid out as the loader would map it, for tests that need image-relative reads without the game running.</summary>
    public static byte[] MapFile(byte[] file)
    {
        var headers = file.AsSpan();
        var mapped = new byte[SizeOfImage(headers)];
        int lfanew = NtHeaders(headers);
        int headersSize = (int)BitConverter.ToUInt32(headers[(lfanew + 24 + 60)..]);
        file.AsSpan(0, Math.Min(headersSize, file.Length)).CopyTo(mapped);
        foreach (var s in Sections(headers))
        {
            int length = (int)Math.Min(s.RawSize, Math.Min(s.VirtualSize == 0 ? s.RawSize : s.VirtualSize, (uint)(mapped.Length - s.Rva)));
            if (s.RawOffset + length <= file.Length && length > 0)
            {
                file.AsSpan((int)s.RawOffset, length).CopyTo(mapped.AsSpan((int)s.Rva));
            }
        }
        return mapped;
    }

    /// <summary>Whether the section is marked executable.</summary>
    public static bool IsExecutable(this PeSection s) => (s.Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;

    /// <summary>Whether the section is initialized data that is readable but neither writable nor executable, such as .rdata.</summary>
    public static bool IsReadOnlyData(this PeSection s) =>
        (s.Characteristics & IMAGE_SCN_CNT_INITIALIZED_DATA) != 0 &&
        (s.Characteristics & IMAGE_SCN_MEM_READ) != 0 &&
        (s.Characteristics & (IMAGE_SCN_MEM_WRITE | IMAGE_SCN_MEM_EXECUTE)) == 0;
}
