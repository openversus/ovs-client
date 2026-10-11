using OpenVersus.Game;

namespace OpenVersus.Tests;

public class GameStringsTests
{
    [Fact]
    public void FStringsReadThroughTheirHeaderAndRefuseNonsense()
    {
        var game = new FakeGame();
        nint text = game.AddFString("Rival Name!");
        Assert.True(GameStrings.TryReadFString(game.Memory, text, out string value));
        Assert.Equal("Rival Name!", value);

        nint empty = game.AddBlock(0x10);
        Assert.True(GameStrings.TryReadFString(game.Memory, empty, out value));
        Assert.Equal("", value);

        nint dangling = game.AddBlock(0x10);
        game.Memory.Write(dangling, 0x300000000L);
        game.Memory.Write(dangling + 8, 5);
        Assert.False(GameStrings.TryReadFString(game.Memory, dangling, out _));

        nint huge = game.AddBlock(0x10);
        game.Memory.Write(huge, (long)text);
        game.Memory.Write(huge + 8, 100000);
        Assert.False(GameStrings.TryReadFString(game.Memory, huge, out _));
        Assert.False(GameStrings.TryReadFString(game.Memory, 0, out _));
    }

    [Fact]
    public void FileNamesKeepOnlySafeCharacters()
    {
        Assert.Equal("Rival_Name", GameStrings.ForFileName("Rival Name!"));
        Assert.Equal("a-b_c", GameStrings.ForFileName("a-b/c"));
        Assert.Equal("", GameStrings.ForFileName("///"));
        Assert.Equal("abcde", GameStrings.ForFileName("abcdefgh", 5));
    }
}
