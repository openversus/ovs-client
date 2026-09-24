using OpenVersus.Memory;

namespace OpenVersus.Tests;

public class PatternScannerTests
{
    [Fact]
    public void FirstMatchWinsAndAllFindsEveryOverlap()
    {
        var p = BytePattern.Parse("AA ? AA");
        byte[] hay = [0x00, 0xAA, 0x01, 0xAA, 0x02, 0xAA, 0x03];
        Assert.Equal(1, PatternScanner.FindFirst(hay, p));
        Assert.Equal(new[] { 1, 3 }, PatternScanner.FindAll(hay, p));
    }

    [Fact]
    public void NoMatchIsMinusOne()
    {
        var p = BytePattern.Parse("DE AD BE EF");
        Assert.Equal(-1, PatternScanner.FindFirst(new byte[] { 0xDE, 0xAD, 0xBE }, p));
        Assert.Empty(PatternScanner.FindAll(new byte[] { 0xDE, 0xAD, 0xBE }, p));
    }

    [Fact]
    public void AnchorInTheMiddleStillFindsAMatchAtOffsetZero()
    {
        var p = BytePattern.Parse("? ? 11 22 33 ?");
        byte[] hay = [0x01, 0x02, 0x11, 0x22, 0x33, 0x04, 0x05];
        Assert.Equal(0, PatternScanner.FindFirst(hay, p));
    }

    [Fact]
    public void MatchAtTheVeryEnd()
    {
        var p = BytePattern.Parse("11 22");
        byte[] hay = [0x00, 0x00, 0x11, 0x22];
        Assert.Equal(2, PatternScanner.FindFirst(hay, p));
    }
}
