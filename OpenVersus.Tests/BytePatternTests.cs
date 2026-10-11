using OpenVersus.Memory;

namespace OpenVersus.Tests;

public class BytePatternTests
{
    [Fact]
    public void SingleQuestionMarkIsOneWildcardByte()
    {
        var p = BytePattern.Parse("48 8D 0D ? ? ? ? E9");
        Assert.Equal(8, p.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0xFF }, p.Mask);
        Assert.Equal(new byte[] { 0x48, 0x8D, 0x0D, 0, 0, 0, 0, 0xE9 }, p.Bytes);
    }

    [Fact]
    public void DoubleQuestionMarkIsTwoWildcardBytesLikeTheCppParser()
    {
        // TransformPattern in Patterns.cpp pushes one wildcard per '?', so "??" is two bytes.
        var p = BytePattern.Parse("E8 ?? C3");
        Assert.Equal(4, p.Length);
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF }, p.Mask);
    }

    [Fact]
    public void SpacingIsIrrelevantAndHexIsCaseInsensitive()
    {
        var a = BytePattern.Parse("48 8b cb 48 8d 15");
        var b = BytePattern.Parse("488BCB488D15");
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.Equal(a.Mask, b.Mask);
    }

    [Fact]
    public void ShippedPatternsParseToTheirByteCounts()
    {
        // From sample.ini. Counting by hand: 23 bytes of "48 8b cb 48 8d 15 ? ? ? ? E8 ? ? ? ? 48 8b 4c 24 ? 48 85 c9" and so on.
        var endpoint = BytePattern.Parse("48 8b cb 48 8d 15 ? ? ? ? E8 ? ? ? ? 48 8b 4c 24 ? 48 85 c9 74 05 E8 ? ? ? ? 48 8b c3 48 8b 5c 24");
        Assert.Equal(37, endpoint.Length);
        var prod = BytePattern.Parse("49 ? ? 48 8d 15 ? ? ? ? E8 ? ? ? ? 49 ? ? ? ? 00 00 48 8b");
        Assert.Equal(24, prod.Length);
        Assert.Equal(0, prod.Mask[1]);
        Assert.Equal(0xFF, prod.Mask[20]);
    }

    [Fact]
    public void AnchorIsTheLongestFixedRun()
    {
        var p = BytePattern.Parse("48 ? ? 8D 15 E8 ? C3");
        Assert.Equal(3, p.AnchorStart);
        Assert.Equal(3, p.AnchorLength);
    }

    [Fact]
    public void AllWildcardsIsAnError()
    {
        Assert.Throws<FormatException>(() => BytePattern.Parse("? ? ?"));
        Assert.Throws<FormatException>(() => BytePattern.Parse(""));
    }

    [Fact]
    public void MatchesUseTheMask()
    {
        var p = BytePattern.Parse("E8 ? ? ? ? C3");
        byte[] hay = [0x90, 0xE8, 1, 2, 3, 4, 0xC3, 0xE8, 9, 9, 9, 9, 0x90];
        Assert.True(p.MatchesAt(hay, 1));
        Assert.False(p.MatchesAt(hay, 7));
        Assert.False(p.MatchesAt(hay, 8));
    }
}
