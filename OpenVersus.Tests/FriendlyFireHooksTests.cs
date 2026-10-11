using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="FriendlyFireHooks"/>'s patterns against the real bytes of the functions and the shield's team check,
/// copied from the game's only build: each pattern finds its bytes, the moved prologues are the functions' first
/// instructions, and the shield's call goes to IsSameTeam. The hooks themselves are proven in the game.
/// </summary>
public unsafe class FriendlyFireHooksTests
{
    // 0x142951b00
    private static readonly byte[] s_processActiveHit = Hex("40 55 56 41 56 48 8D AC 24 30 F5 FF FF 48 81 EC D0 0B 00 00");
    // 0x142957c10
    private static readonly byte[] s_getHitResponseFlags = Hex("48 89 5C 24 10 48 89 6C 24 18 57 41 54 41 55 41 56 41 57 48 81 EC E0 0A 00 00");
    // 0x14121bfb0
    private static readonly byte[] s_isSameTeam = Hex("48 85 D2 74 1F 80 7A 30 00 75 19 F7 42 08 00 00 00 60 75 10 8B 89 D0 00 00 00");
    // 0x142951a00
    private static readonly byte[] s_getTopLevelAttacker = Hex("48 89 5C 24 10 56 48 83 EC 20 48 8B F1 48 8B 89 A0 00 00 00");
    // 0x142953875 to 0x14295388c: the shield's team check, mov rdx, r14; call IsSameTeam
    private const long ShieldStart = 0x142953875;
    private const long IsSameTeam = 0x14121bfb0;
    private static readonly byte[] s_shield = Hex("80 79 30 00 75 19 F7 41 08 00 00 00 60 75 10 49 8B D6 E8 24 87 8C FE");

    internal static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", ""));

    private static int Match(byte[] bytes, string pattern) => PatternScanner.FindFirst(bytes, BytePattern.Parse(pattern));

    [Fact]
    public void EachPatternFindsItsFunction()
    {
        Assert.Equal(0, Match(s_processActiveHit, FriendlyFireHooks.ProcessActiveHitInteractionPattern));
        Assert.Equal(0, Match(s_getHitResponseFlags, FriendlyFireHooks.GetHitResponseFlagsPattern));
        Assert.Equal(0, Match(s_isSameTeam, FriendlyFireHooks.IsSameTeamPattern));
        Assert.Equal(0, Match(s_getTopLevelAttacker, FriendlyFireHooks.GetTopLevelAttackerPattern));
        Assert.Equal(0, Match(s_shield, FriendlyFireHooks.ShieldTeamCheckPattern));
    }

    [Fact]
    public void TheMovedProloguesAreTheFunctionsFirstInstructions()
    {
        Assert.Equal(FriendlyFireHooks.s_processActiveHitPrologue, s_processActiveHit[..FriendlyFireHooks.s_processActiveHitPrologue.Length]);
        Assert.Equal(FriendlyFireHooks.s_getHitResponseFlagsPrologue, s_getHitResponseFlags[..FriendlyFireHooks.s_getHitResponseFlagsPrologue.Length]);
    }

    [Fact]
    public void TheShieldCallGoesToIsSameTeam()
    {
        int call = Match(s_shield, FriendlyFireHooks.ShieldTeamCheckPattern) + FriendlyFireHooks.ShieldTeamCheckCall;
        Assert.Equal(CallSite.CallOpcode, s_shield[call]);
        fixed (byte* p = s_shield)
        {
            Assert.Equal(IsSameTeam - ShieldStart, CallSite.Destination((nint)(p + call)) - (nint)p);
        }
    }

    [SkippableFact]
    public void EachPatternIsUniqueInTheFinalBuildAndWhereTheCxxClientHadIt()
    {
        byte[] mapped = FinalBuild.Mapped();

        Assert.Equal(0x02951B00, FinalBuild.Single(mapped, FriendlyFireHooks.ProcessActiveHitInteractionPattern));
        Assert.Equal(0x02957C10, FinalBuild.Single(mapped, FriendlyFireHooks.GetHitResponseFlagsPattern));
        Assert.Equal(0x0121BFB0, FinalBuild.Single(mapped, FriendlyFireHooks.IsSameTeamPattern));
        Assert.Equal(0x02951A00, FinalBuild.Single(mapped, FriendlyFireHooks.GetTopLevelAttackerPattern));
        Assert.Equal(0x02953887, FinalBuild.Single(mapped, FriendlyFireHooks.ShieldTeamCheckPattern) + FriendlyFireHooks.ShieldTeamCheckCall);
    }
}

/// <summary>The game's executable, for the checks that need the whole image: skipped where it is not installed.</summary>
internal static class FinalBuild
{
    /// <summary>OVS_GAME_EXE, or Steam's Linux install under HOME.</summary>
    public static string Exe =>
        Environment.GetEnvironmentVariable("OVS_GAME_EXE") is { Length: > 0 } exe ? exe
        : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe");

    /// <summary>The executable as the loader maps it; skips the test when it is not installed.</summary>
    public static byte[] Mapped()
    {
        Skip.If(!File.Exists(Exe), "game exe not installed here (set OVS_GAME_EXE)");
        return PeImage.MapFile(File.ReadAllBytes(Exe));
    }

    /// <summary>The RVA of the pattern's one match; fails when it matches more than once or not at all.</summary>
    public static int Single(byte[] mapped, string pattern) => Assert.Single(PatternScanner.FindAll(mapped, BytePattern.Parse(pattern)));
}
