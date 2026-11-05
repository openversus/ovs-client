using OpenVersus.Memory;
using OpenVersus.Native;

namespace OpenVersus.Game;

/// <summary>The host executable as mapped in this process.</summary>
public sealed unsafe class GameImage
{
    /// <summary>Where the image is mapped.</summary>
    public nint Base { get; }
    /// <summary>SizeOfImage from the PE headers.</summary>
    public int Size { get; }
    /// <summary>The PE section table.</summary>
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

    /// <summary>The executable this process was started from.</summary>
    public static GameImage Host() => new(Kernel32.GetModuleHandle(null));

    /// <summary>Base to SizeOfImage: what the C++ pattern scan covered.</summary>
    public ReadOnlySpan<byte> Bytes => new((void*)Base, Size);

    /// <summary>The address of <paramref name="rva"/> in this image.</summary>
    public nint Address(uint rva) => Base + (nint)rva;

    /// <paramref name="address"/> relative to <see cref="Base"/>; throws <see cref="OverflowException"/> when it is below the base or more than 4 GB past it.
    public uint Rva(nint address) => checked((uint)(address - Base));

    /// <summary>Whether <paramref name="address"/> lies between <see cref="Base"/> and the end of the image.</summary>
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
