namespace OpenVersus.Memory;

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
			throw new FormatException("not a PE image (no MZ)");
		int lfanew = BitConverter.ToInt32(headers[0x3C..]);
		if (BitConverter.ToUInt32(headers[lfanew..]) != 0x00004550)
			throw new FormatException("not a PE image (no PE signature)");
		return lfanew;
	}

	public static uint SizeOfImage(ReadOnlySpan<byte> headers)
	{
		int lfanew = NtHeaders(headers);
		return BitConverter.ToUInt32(headers[(lfanew + 24 + 56)..]);
	}

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

	public static bool IsExecutable(this PeSection s) => (s.Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;

	public static bool IsReadOnlyData(this PeSection s) =>
		(s.Characteristics & IMAGE_SCN_CNT_INITIALIZED_DATA) != 0 &&
		(s.Characteristics & IMAGE_SCN_MEM_READ) != 0 &&
		(s.Characteristics & (IMAGE_SCN_MEM_WRITE | IMAGE_SCN_MEM_EXECUTE)) == 0;
}
