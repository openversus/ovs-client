using OpenVersus.Memory;
using OpenVersus.Native;

namespace OpenVersus.Game;

/// <summary>The host executable as mapped in this process.</summary>
public sealed unsafe class GameImage
{
    public nint Base { get; }
    public int Size { get; }
    public IReadOnlyList<PeSection> Sections { get; }

    private GameImage(nint @base)
    {
        Base = @base;
        var headers = PeImage.HeadersInMemory((byte*)@base);
        Size = checked((int)PeImage.SizeOfImage(headers));
        Sections = PeImage.Sections(headers);
    }

    /// <summary>An image described rather than mapped, for tests of the code that only needs its bounds.</summary>
    internal GameImage(nint @base, int size, IReadOnlyList<PeSection> sections)
    {
        Base = @base;
        Size = size;
        Sections = sections;
    }

    public static GameImage Host() => new(Kernel32.GetModuleHandle(null));

    /// <summary>Base to SizeOfImage: what the C++ pattern scan covered.</summary>
    public ReadOnlySpan<byte> Bytes => new((void*)Base, Size);

    public nint Address(uint rva) => Base + (nint)rva;

    public uint Rva(nint address) => checked((uint)(address - Base));

    public bool Contains(nint address) => address >= Base && address < Base + Size;

    /// <summary>
    /// FNV-1a over the .text section's virtual size, as the C++ HashTextSectionOfHost. Its top
    /// 32 bits name the pattern-cache section.
    /// </summary>
    public ulong HashTextSection()
    {
        foreach (var s in Sections)
        {
            if (s.Name != ".text")
            {
                continue;
            }

            ReadOnlySpan<byte> data = new((void*)(Base + (nint)s.Rva), checked((int)s.VirtualSize));
            ulong hash = 14695981039346656037UL;
            foreach (byte b in data)
            {
                hash = (hash ^ b) * 1099511628211UL;
            }

            return hash;
        }
        return 0;
    }
}
