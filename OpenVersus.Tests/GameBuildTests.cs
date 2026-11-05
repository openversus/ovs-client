using OpenVersus.Config;
using OpenVersus.Game;
using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="GameBuild"/>: Steam's values are the ones the client always had, and against the real executables each
/// build-specific pattern, prologue and address is where the hooks expect it (OVS_GAME_EXE for Steam's, OVS_EPIC_GAME_EXE
/// for the Epic Games Store's; skipped when they are not installed).
/// </summary>
public class GameBuildTests
{
    [Fact]
    public void SteamsBuildKeepsTheAddressesTheClientAlwaysHad()
    {
        Assert.Equal(0x081C5090u, GameBuild.Steam.ObjectArrayRva);
        Assert.Equal(0x02D3D810u, GameBuild.Steam.ProcessEventRva);
        Assert.Equal(0x06795E20u, GameBuild.Steam.UFunctionVTableNativeRva);
        Assert.Equal(0x06795A90u, GameBuild.Steam.UFunctionVTableBlueprintRva);
        Assert.False(GameBuild.Steam.IsEpic);
        Assert.True(GameBuild.Epic.IsEpic);
    }

    [SkippableFact]
    public void TheEpicPatternsFindOnlyEpicsFunctions()
    {
        byte[] epic = EpicBuild.Mapped();
        Assert.Equal(0x028F7690, FinalBuild.Single(epic, StockRulesHooks.EpicPlayerDiedPattern));
        Assert.Equal(0x028BC9E8, FinalBuild.Single(epic, FriendlyFireHooks.EpicShieldTeamCheckPattern));
        Assert.Empty(PatternScanner.FindAll(epic, BytePattern.Parse(StockRulesHooks.PlayerDiedPattern)));
        Assert.Empty(PatternScanner.FindAll(epic, BytePattern.Parse(FriendlyFireHooks.ShieldTeamCheckPattern)));
    }

    [SkippableFact]
    public void TheSteamPatternsDoNotFindEpicsOnSteam()
    {
        byte[] steam = FinalBuild.Mapped();
        Assert.Empty(PatternScanner.FindAll(steam, BytePattern.Parse(StockRulesHooks.EpicPlayerDiedPattern)));
        Assert.Empty(PatternScanner.FindAll(steam, BytePattern.Parse(FriendlyFireHooks.EpicShieldTeamCheckPattern)));
    }

    [SkippableFact]
    public void EpicsProloguesAndProcessEventAreWhereTheHooksExpectThem()
    {
        byte[] epic = EpicBuild.Mapped();
        Assert.Equal(StockRulesHooks.s_epicPlayerDiedPrologue, epic.AsSpan(0x028F7690, 9).ToArray());
        int respawn = FinalBuild.Single(epic, StockRulesHooks.RespawnPattern);
        Assert.Equal(StockRulesHooks.s_epicRespawnPrologue, epic.AsSpan(respawn, 9).ToArray());
        Assert.Equal(new byte[] { 0x40, 0x55, 0x56, 0x57, 0x41, 0x54 }, epic.AsSpan((int)GameBuild.Epic.ProcessEventRva, 6).ToArray());
        // The shield's call goes to IsSameTeam, which Steam's pattern finds in Epic's build too.
        int isSameTeam = FinalBuild.Single(epic, FriendlyFireHooks.IsSameTeamPattern);
        int call = 0x028BC9E8 + FriendlyFireHooks.ShieldTeamCheckCall;
        Assert.Equal(isSameTeam, call + 5 + BitConverter.ToInt32(epic, call + 1));
    }

    /// <summary>
    /// The build each real executable is taken for: by its .text hash; with an unknown hash, by its PE timestamp; and with
    /// ProcessEvent's first bytes patched too (another mod's ProcessEvent hook), still by the timestamp.
    /// </summary>
    [SkippableFact]
    public void EachExecutableIsTakenForItsOwnBuild()
    {
        foreach (var (mapped, build) in new[] { (FinalBuild.Mapped(), GameBuild.Steam), (EpicBuild.Mapped(), GameBuild.Epic) })
        {
            WithImage(mapped, image =>
            {
                Assert.Same(build, GameBuild.Detect(image.HashTextSection(), image));
                Assert.Same(build, GameBuild.Detect(0, image));
            });
            mapped[(int)build.ProcessEventRva] = 0xE9;
            WithImage(mapped, image => Assert.Same(build, GameBuild.Detect(0, image)));
        }
    }

    // A GameImage over a mapped executable held in managed memory, pinned while it is used.
    private static unsafe void WithImage(byte[] mapped, Action<GameImage> use)
    {
        fixed (byte* at = mapped)
        {
            use(new GameImage((nint)at, mapped.Length, PeImage.Sections(PeImage.HeadersInMemory(at))));
        }
    }

    /// <summary>The notification manager getter's fallback in each build is where the Notifications pattern's call goes.</summary>
    [SkippableFact]
    public void EachBuildsNotificationManagerFallbackIsThePatternsCall()
    {
        var pattern = BytePattern.Parse(Settings.Rows.NotificationsPattern.Default);
        foreach (var (image, rva) in new[] { (FinalBuild.Mapped(), GameFunctions.GetNotificationManagerRva), (EpicBuild.Mapped(), GameFunctions.EpicGetNotificationManagerRva) })
        {
            int match = PatternScanner.FindFirst(image, pattern);
            Assert.True(match >= 0);
            int call = match + 19;
            Assert.Equal((int)rva, call + 5 + BitConverter.ToInt32(image, call + 1));
        }
    }
}

/// <summary>The Epic Games Store's executable, as <see cref="FinalBuild"/> is Steam's.</summary>
internal static class EpicBuild
{
    public static string Exe => Environment.GetEnvironmentVariable("OVS_EPIC_GAME_EXE") ?? "";

    public static byte[] Mapped()
    {
        Skip.If(!File.Exists(Exe), "the Epic Games Store's game exe is not here (set OVS_EPIC_GAME_EXE)");
        return PeImage.MapFile(File.ReadAllBytes(Exe));
    }
}
