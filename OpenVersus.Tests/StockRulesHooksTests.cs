using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="StockRulesHooks"/>'s patterns against the real bytes of the six functions' starts, copied from the
/// game's only build: each pattern finds its function and the moved prologues are its first instructions. The
/// hooks themselves are proven in the game.
/// </summary>
public class StockRulesHooksTests
{
    // 0x142966870
    private static readonly byte[] s_playerDied = FriendlyFireHooksTests.Hex("48 85 D2 0F 84 9A 05 00 00 48 8B C4 48 89 48 08 55 41 54 41 56");
    // 0x142966e20
    private static readonly byte[] s_registerCharacter = FriendlyFireHooksTests.Hex("40 53 56 57 48 81 EC C0 00 00 00 48 8B 05 B6 D1 61 05 48 33 C4 48 89 84 24 B0 00 00 00 48 8B FA");
    // 0x1411e4f70
    private static readonly byte[] s_doRespawn = FriendlyFireHooksTests.Hex("8B 81 80 03 00 00 85 C0 7E 08 FF C8 89 81 80 03 00 00");
    // 0x142967be0
    private static readonly byte[] s_attemptEndMatch = FriendlyFireHooksTests.Hex("40 56 48 83 EC 50 80 B9 E0 00 00 00 00 48 8B F1");
    // 0x1411fc980
    private static readonly byte[] s_setRespawnsRemaining = FriendlyFireHooksTests.Hex("89 91 80 03 00 00 C3 CC CC CC CC CC CC CC CC CC");
    // 0x142966fc0
    private static readonly byte[] s_respawn = FriendlyFireHooksTests.Hex("48 85 D2 0F 84 E3 02 00 00 55 41 56 41 57 48 8D 6C 24 B9 48 81 EC 00 01 00 00");

    public static TheoryData<string, byte[], byte[]?> Functions => new()
    {
        { StockRulesHooks.PlayerDiedPattern, s_playerDied, StockRulesHooks.s_playerDiedPrologue },
        { StockRulesHooks.RegisterCharacterPattern, s_registerCharacter, StockRulesHooks.s_registerCharacterPrologue },
        { StockRulesHooks.DoRespawnPattern, s_doRespawn, StockRulesHooks.s_doRespawnPrologue },
        { StockRulesHooks.AttemptEndMatchPattern, s_attemptEndMatch, StockRulesHooks.s_attemptEndMatchPrologue },
        { StockRulesHooks.SetRespawnsRemainingPattern, s_setRespawnsRemaining, null },
        { StockRulesHooks.RespawnPattern, s_respawn, StockRulesHooks.s_respawnPrologue },
    };

    [Theory]
    [MemberData(nameof(Functions))]
    public void EachPatternFindsItsFunctionAndThePrologueIsItsStart(string pattern, byte[] function, byte[]? prologue)
    {
        Assert.Equal(0, PatternScanner.FindFirst(function, BytePattern.Parse(pattern)));
        if (prologue != null)
        {
            Assert.Equal(prologue, function[..prologue.Length]);
        }
    }

    [Fact]
    public void TheConditionalJumpProloguesEndInARel32Jcc()
    {
        // PlayerDied and Respawn are hooked with endsInConditionalJump: their prologue's last six bytes are 0F 8x rel32.
        foreach (byte[] prologue in new[] { StockRulesHooks.s_playerDiedPrologue, StockRulesHooks.s_respawnPrologue })
        {
            Assert.Equal(0x0F, prologue[^6]);
            Assert.Equal(0x80, prologue[^5] & 0xF0);
        }
    }

    [SkippableFact]
    public void EachPatternIsUniqueInTheFinalBuildAndWhereTheCxxClientHadIt()
    {
        byte[] mapped = FinalBuild.Mapped();

        Assert.Equal(0x02966870, FinalBuild.Single(mapped, StockRulesHooks.PlayerDiedPattern));
        Assert.Equal(0x02966E20, FinalBuild.Single(mapped, StockRulesHooks.RegisterCharacterPattern));
        Assert.Equal(0x011E4F70, FinalBuild.Single(mapped, StockRulesHooks.DoRespawnPattern));
        Assert.Equal(0x02967BE0, FinalBuild.Single(mapped, StockRulesHooks.AttemptEndMatchPattern));
        Assert.Equal(0x011FC980, FinalBuild.Single(mapped, StockRulesHooks.SetRespawnsRemainingPattern));
        Assert.Equal(0x02966FC0, FinalBuild.Single(mapped, StockRulesHooks.RespawnPattern));
    }
}
