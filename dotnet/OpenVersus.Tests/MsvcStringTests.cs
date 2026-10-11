using System.Text;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

public unsafe class MsvcStringTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJpbnN0YWxsSWQiOiJ4In0.c2lnbmF0dXJl";

    /// <summary>Reads an MSVC std::string the way game code does: the pointer when the capacity is 16 or more, else the inline bytes.</summary>
    private static string ReadMsvc(nint s)
    {
        ulong size = *(ulong*)(s + 0x10), capacity = *(ulong*)(s + 0x18);
        byte* chars = capacity >= 16 ? *(byte**)s : (byte*)s;
        return Encoding.UTF8.GetString(chars, checked((int)size));
    }

    [Fact]
    public void AShortStringIsStoredInlineAndALongOneThroughAPointer()
    {
        var small = MsvcString.Create("pc");
        Assert.Equal(15UL, *(ulong*)(small.Address + 0x18));
        Assert.Equal("pc", ReadMsvc(small.Address));

        var large = MsvcString.Create(Token);
        Assert.Equal((ulong)Token.Length, *(ulong*)(large.Address + 0x10));
        Assert.True(*(ulong*)(large.Address + 0x18) >= 16);
        Assert.Equal(Token, ReadMsvc(large.Address));
        Assert.Equal(0, (*(byte**)large.Address)[Token.Length]);
    }

    [Fact]
    public void AStringOfExactlySixteenCharactersIsNotInline()
    {
        // Sixteen characters and a terminator do not fit the 16-byte inline buffer.
        var s = MsvcString.Create("x-hydra-platform");
        Assert.Equal(16UL, *(ulong*)(s.Address + 0x18));
        Assert.Equal("x-hydra-platform", ReadMsvc(s.Address));
    }
}
