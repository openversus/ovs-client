using System.Runtime.InteropServices;
using System.Text;

namespace OpenVersus.Memory;

/// <summary>
/// A <c>std::string</c> of our own in the layout MSVC's x64 release library uses, so game code can
/// read it as if it were its own: 16 bytes holding either the characters (15 or fewer, "small
/// string") or a pointer to them, then the size at +0x10 and the capacity at +0x18. A capacity of
/// 16 or more tells the reader to follow the pointer. Hand one only to game code that copies from
/// it; code that takes ownership would later free our memory with its own allocator.
/// </summary>
public sealed unsafe class MsvcString
{
    /// <summary>The size of the object itself.</summary>
    public const int ObjectSize = 0x20;
    private const int SmallCapacity = 15;

    /// <summary>The text, as given.</summary>
    public string Text { get; }
    /// <summary>Where the object is: what game code expects a <c>const std::string&amp;</c> to point at.</summary>
    public nint Address { get; }

    private MsvcString(string text, nint address)
    {
        Text = text;
        Address = address;
    }

    /// <summary>
    /// Builds one for <paramref name="text"/>, stored as UTF-8. It is never freed: game code may be
    /// reading it on another thread when the caller moves on to a new one, and each is small.
    /// </summary>
    public static MsvcString Create(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var obj = (byte*)NativeMemory.AllocZeroed(ObjectSize);
        if (bytes.Length <= SmallCapacity)
        {
            bytes.CopyTo(new Span<byte>(obj, bytes.Length));
            *(ulong*)(obj + 0x18) = SmallCapacity;
        }
        else
        {
            var chars = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(chars, bytes.Length));
            *(byte**)obj = chars;
            *(ulong*)(obj + 0x18) = (ulong)bytes.Length;
        }

        *(ulong*)(obj + 0x10) = (ulong)bytes.Length;
        return new MsvcString(text, (nint)obj);
    }
}
