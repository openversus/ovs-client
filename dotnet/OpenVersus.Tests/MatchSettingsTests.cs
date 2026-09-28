using System.Text;
using OpenVersus.Game;

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
