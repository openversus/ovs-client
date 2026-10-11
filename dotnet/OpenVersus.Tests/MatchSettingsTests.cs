using System.Text;
using OpenVersus.Game;
using OpenVersus.Hooks;

namespace OpenVersus.Tests;

public class MatchSettingsTests
{
    private const long Config = 0x1_0000_0000;

    [Fact]
    public void TheModeRingoutsAndSelectedMutatorsAreRead()
    {
        var memory = new FakeMemory();
        memory.Alloc((nint)Config, 0x200);
        WriteFString(memory, (nint)Config + Mvs.GameplayConfigModeString, 0x2_0000_0000, "2v2");
        memory.Write((nint)Config + Mvs.GameplayConfigNumRingouts, 4);
        memory.Write((nint)Config + Mvs.GameplayConfigMatchType, (byte)5);
        memory.Write((nint)Config + Mvs.GameplayConfigIsOnlineMatch, (byte)1);

        // WorldBuffs: two assets, each with its slug at +0x60, and a null entry.
        const long array = 0x3_0000_0000, first = 0x4_0000_0000, second = 0x5_0000_0000;
        memory.Alloc((nint)array, 24);
        memory.Write((nint)array, first);
        memory.Write((nint)array + 8, 0L);
        memory.Write((nint)array + 16, second);
        memory.Write((nint)Config + Mvs.GameplayConfigWorldBuffs, array);
        memory.Write((nint)Config + Mvs.GameplayConfigWorldBuffs + 8, 3);
        memory.Alloc((nint)first, 0x80);
        memory.Alloc((nint)second, 0x80);
        WriteFString(memory, (nint)first + Mvs.HydraSyncDataAssetSlug, 0x6_0000_0000, "ovs_friendly_fire");
        WriteFString(memory, (nint)second + Mvs.HydraSyncDataAssetSlug, 0x7_0000_0000, "ovs_2v2_individual_stocks");

        Assert.True(MatchSettings.TryRead(memory, (nint)Config, out var settings));
        Assert.Equal("2v2", settings.Mode);
        Assert.Equal(4, settings.NumRingouts);
        Assert.Equal(5, settings.MatchType);
        Assert.True(settings.Online);
        Assert.Equal(["ovs_friendly_fire", "", "ovs_2v2_individual_stocks"], settings.WorldBuffs);
        Assert.True(settings.HasWorldBuff("OVS_Friendly_Fire"));
        Assert.False(settings.HasWorldBuff("ovs_something_else"));
    }

    [Fact]
    public void AnImplausibleWorldBuffArrayReadsAsNone()
    {
        var memory = new FakeMemory();
        memory.Alloc((nint)Config, 0x200);
        WriteFString(memory, (nint)Config + Mvs.GameplayConfigModeString, 0x2_0000_0000, "FFA");
        memory.Write((nint)Config + Mvs.GameplayConfigWorldBuffs, 0x3_0000_0000L);
        memory.Write((nint)Config + Mvs.GameplayConfigWorldBuffs + 8, MatchSettings.MaxWorldBuffs + 1);

        Assert.True(MatchSettings.TryRead(memory, (nint)Config, out var settings));
        Assert.Empty(settings.WorldBuffs);
    }

    [Fact]
    public void NoConfigOrNoModeIsNothing()
    {
        var memory = new FakeMemory();
        Assert.False(MatchSettings.TryRead(memory, 0, out _));
        Assert.False(MatchSettings.TryRead(memory, (nint)Config, out _));
    }

    [Fact]
    public void FriendlyFireIsOnWithTheMutatorOrOfflineWhenTesting()
    {
        var withMutator = new MatchSettings("2v2", 4, ["ovs_friendly_fire"], 5, true);
        var online = new MatchSettings("2v2", 4, [], 5, true);
        var localPlay = new MatchSettings("2v2", 4, [], MatchSettings.LocalPlayMatchType, false);
        var lab = new MatchSettings("1v1", 4, [], MatchSettings.LabMatchType, false);

        Assert.True(FriendlyFireHooks.IsOn(withMutator, offlineTesting: false));
        Assert.False(FriendlyFireHooks.IsOn(online, offlineTesting: true));
        Assert.False(FriendlyFireHooks.IsOn(localPlay, offlineTesting: false));
        Assert.True(FriendlyFireHooks.IsOn(localPlay, offlineTesting: true));
        Assert.True(FriendlyFireHooks.IsOn(lab, offlineTesting: true));
        // An online match never takes it from the setting, whatever its type says.
        Assert.False(FriendlyFireHooks.IsOn(localPlay with { Online = true }, offlineTesting: true));
    }

    [Fact]
    public void BetaSpeedRunsEveryMatchWithTheMutatorAt110()
    {
        var betaSpeed = new MatchSettings("2v2", 4, ["ovs_friendly_fire", "ovs_beta_speed"], 5, true);
        var online = new MatchSettings("2v2", 4, [], 5, true);
        var lab = new MatchSettings("1v1", 4, [], MatchSettings.LabMatchType, false);

        Assert.Equal(110, GameSpeedHooks.PercentFor(betaSpeed, offlinePercent: 50));
        Assert.Equal(110, GameSpeedHooks.PercentFor(lab with { WorldBuffs = ["OVS_BETA_SPEED"] }, offlinePercent: 100));
        Assert.Equal(100, GameSpeedHooks.PercentFor(online, offlinePercent: 50));
        Assert.Equal(50, GameSpeedHooks.PercentFor(lab, offlinePercent: 50));
        Assert.Equal(100, GameSpeedHooks.PercentFor(lab with { Online = true }, offlinePercent: 50));
    }

    [Fact]
    public void BetaSpeedRunsAtTheServersSpeedOnceItHasIt()
    {
        var betaSpeed = new MatchSettings("1v1", 4, ["ovs_beta_speed", "ovs_shield_hp"], 5, true);
        var lab = new MatchSettings("1v1", 4, [], MatchSettings.LabMatchType, false);
        try
        {
            Assert.True(GameSpeedHooks.SetBetaSpeed(120));
            Assert.Equal(120, GameSpeedHooks.BetaSpeedPercent);
            Assert.Equal(120, GameSpeedHooks.PercentFor(betaSpeed, offlinePercent: 50));
            // The offline setting is its own.
            Assert.Equal(50, GameSpeedHooks.PercentFor(lab, offlinePercent: 50));

            // Outside the server's own range (50 to 200): kept as it was.
            Assert.False(GameSpeedHooks.SetBetaSpeed(201));
            Assert.False(GameSpeedHooks.SetBetaSpeed(49));
            Assert.Equal(120, GameSpeedHooks.PercentFor(betaSpeed, offlinePercent: 50));
        }
        finally
        {
            GameSpeedHooks.SetBetaSpeed(GameSpeedHooks.DefaultBetaSpeedPercent);
        }
    }

    /// <summary>An FString at <paramref name="at"/>: data, count with the terminator, capacity.</summary>
    private static void WriteFString(FakeMemory memory, nint at, long data, string text)
    {
        byte[] chars = Encoding.Unicode.GetBytes(text + "\0");
        chars.CopyTo(memory.Alloc((nint)data, chars.Length), 0);
        memory.Write(at, data);
        memory.Write(at + 8, text.Length + 1);
        memory.Write(at + 12, text.Length + 1);
    }
}
