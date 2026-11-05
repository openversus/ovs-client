using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="FriendlyFireHooks"/>'s patterns against the real bytes of the functions, the shield's team check and the
/// hit resolution's response-flags call, copied from the game's only build: each pattern finds its bytes, and both calls
/// go where the hooks expect. The hooks themselves are proven in the game.
/// </summary>
public unsafe class FriendlyFireHooksTests
{
    // 0x142957c10
    private static readonly byte[] s_getHitResponseFlags = Hex("48 89 5C 24 10 48 89 6C 24 18 57 41 54 41 55 41 56 41 57 48 81 EC E0 0A 00 00");
    // 0x14121bfb0
    private static readonly byte[] s_isSameTeam = Hex("48 85 D2 74 1F 80 7A 30 00 75 19 F7 42 08 00 00 00 60 75 10 8B 89 D0 00 00 00");
    // 0x142953875 to 0x14295388c: the shield's team check, mov rdx, r14; call IsSameTeam
    private const long ShieldStart = 0x142953875;
    private const long IsSameTeam = 0x14121bfb0;
    private static readonly byte[] s_shield = Hex("80 79 30 00 75 19 F7 41 08 00 00 00 60 75 10 49 8B D6 E8 24 87 8C FE");
    // 0x1426155e7 to 0x1426155fd: lea r9, [rsi+0x37C]; movzx r8d, bl; mov rdx, rax; mov rcx, rdi; call GetHitResponseFlags
    private const long ResolveStart = 0x1426155e7;
    private const long GetHitResponseFlags = 0x142957c10;
    private static readonly byte[] s_resolve = Hex("4C 8D 8E 7C 03 00 00 44 0F B6 C3 48 8B D0 48 8B CF E8 13 26 34 00");

    internal static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", ""));

    private static int Match(byte[] bytes, string pattern) => PatternScanner.FindFirst(bytes, BytePattern.Parse(pattern));

    [Fact]
    public void EachPatternFindsItsFunction()
    {
        Assert.Equal(0, Match(s_getHitResponseFlags, FriendlyFireHooks.GetHitResponseFlagsPattern));
        Assert.Equal(0, Match(s_isSameTeam, FriendlyFireHooks.IsSameTeamPattern));
        Assert.Equal(0, Match(s_shield, FriendlyFireHooks.ShieldTeamCheckPattern));
        Assert.Equal(0, Match(s_resolve, FriendlyFireHooks.ResolveResponseCallPattern));
    }

    [Fact]
    public void TheResolutionCallGoesToGetHitResponseFlags()
    {
        int call = Match(s_resolve, FriendlyFireHooks.ResolveResponseCallPattern) + FriendlyFireHooks.ResolveResponseCall;
        Assert.Equal(CallSite.CallOpcode, s_resolve[call]);
        fixed (byte* p = s_resolve)
        {
            Assert.Equal(GetHitResponseFlags - ResolveStart, CallSite.Destination((nint)(p + call)) - (nint)p);
        }
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

        Assert.Equal(0x02957C10, FinalBuild.Single(mapped, FriendlyFireHooks.GetHitResponseFlagsPattern));
        Assert.Equal(0x0121BFB0, FinalBuild.Single(mapped, FriendlyFireHooks.IsSameTeamPattern));
        Assert.Equal(0x02953887, FinalBuild.Single(mapped, FriendlyFireHooks.ShieldTeamCheckPattern) + FriendlyFireHooks.ShieldTeamCheckCall);
        Assert.Equal(0x026155F8, FinalBuild.Single(mapped, FriendlyFireHooks.ResolveResponseCallPattern) + FriendlyFireHooks.ResolveResponseCall);
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
