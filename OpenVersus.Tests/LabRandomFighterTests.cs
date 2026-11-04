using OpenVersus.Game;
using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="LabRandomFighter"/>: its patterns against the real bytes of UMvsLocalPlaySetupData::BeginMatch in both builds
/// (Steam 0x1427876c0, Epic Games Store 0x1425bd510, copied from each executable), and its walk of the setup's Players map
/// against fake memory, including maps it must refuse.
/// </summary>
public class LabRandomFighterTests
{
    private static readonly byte[] s_steam = FriendlyFireHooksTests.Hex(
        "40 53 55 57 41 54 41 55 48 83 EC 40 48 8B 01 48 8B E9 FF 90 88 01 00 00 48 8B B8 C8 01 00 00 E8 DC 1A BE FF 45 33 E4 48");
    private static readonly byte[] s_epic = FriendlyFireHooksTests.Hex(
        "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 48 83 EC 40 48 8B 01 48 8B F9 FF 90 88 01 00 00 48 8B A8 C8 01 00 00 E8 24 9E D9 FF 33 F6 48 8B");

    [Fact]
    public void EachPatternFindsItsBuildsBeginMatchAndOnlyThat()
    {
        Assert.Equal(0, PatternScanner.FindFirst(s_steam, BytePattern.Parse(LabRandomFighter.BeginMatchPattern)));
        Assert.Equal(0, PatternScanner.FindFirst(s_epic, BytePattern.Parse(LabRandomFighter.EpicBeginMatchPattern)));
        Assert.True(PatternScanner.FindFirst(s_epic, BytePattern.Parse(LabRandomFighter.BeginMatchPattern)) < 0);
        Assert.True(PatternScanner.FindFirst(s_steam, BytePattern.Parse(LabRandomFighter.EpicBeginMatchPattern)) < 0);
    }

    [Fact]
    public void EachPrologueIsWholeInstructionsAtTheStartOfItsFunction()
    {
        // push rbx (40 53), push rbp, push rdi, push r12 (41 54), push r13 (41 55); mov [rsp+10h], rbx. No rip-relative operand.
        Assert.Equal(LabRandomFighter.s_beginMatchPrologue, s_steam[..LabRandomFighter.s_beginMatchPrologue.Length]);
        Assert.Equal(LabRandomFighter.s_epicBeginMatchPrologue, s_epic[..LabRandomFighter.s_epicBeginMatchPrologue.Length]);
        Assert.True(LabRandomFighter.s_epicBeginMatchPrologue.Length >= 5);
    }

    [SkippableFact]
    public void TheSteamPatternIsUniqueInTheSteamBuildWhereTheDisassemblyFoundIt()
    {
        byte[] mapped = FinalBuild.Mapped();
        Assert.Equal(0x027876C0, FinalBuild.Single(mapped, LabRandomFighter.BeginMatchPattern));
        Assert.True(PatternScanner.FindFirst(mapped, BytePattern.Parse(LabRandomFighter.EpicBeginMatchPattern)) < 0);
    }

    [SkippableFact]
    public void TheEpicPatternIsUniqueInTheEpicBuildWhereTheDisassemblyFoundIt()
    {
        byte[] mapped = EpicBuild.Mapped();
        Assert.Equal(0x025BD510, FinalBuild.Single(mapped, LabRandomFighter.EpicBeginMatchPattern));
        Assert.True(PatternScanner.FindFirst(mapped, BytePattern.Parse(LabRandomFighter.BeginMatchPattern)) < 0);
    }

    [Fact]
    public void AMapElementIsTheKeyTheValueAndTheHashLinks()
    {
        Assert.Equal(0x168, LabRandomFighter.PlayerElementSize);
        Assert.Equal(0x158, Mvs.GameplayPlayerDataSize);
    }

    // A setup object with a players map, its elements somewhere else.
    private const long Setup = 0x10000;
    private const long Elements = 0x20000;
    private const long HeapBits = 0x30000;
    private const long Random = 0x5000;
    private const long Jason = 0x5100;
    private static nint Map => (nint)(Setup + LabRandomFighter.SetupDataPlayers);

    private static FakeMemory MapWith(long[] characters, uint allocated, int? count = null, int? max = null, int? numBits = null, bool heap = false)
    {
        var memory = new FakeMemory();
        memory.Alloc((nint)Setup, 0x400);
        memory.Alloc((nint)Elements, LabRandomFighter.PlayerElementSize * characters.Length);
        memory.Write(Map, Elements);
        memory.Write(Map + 8, count ?? characters.Length);
        memory.Write(Map + 12, max ?? characters.Length);
        if (heap)
        {
            memory.Alloc((nint)HeapBits, 16);
            memory.Write(Map + LabRandomFighter.AllocationFlagsHeap, HeapBits);
            memory.Write((nint)HeapBits, allocated);
        }
        else
        {
            memory.Write(Map + LabRandomFighter.AllocationFlagsInline, allocated);
        }

        memory.Write(Map + LabRandomFighter.AllocationFlagsNumBits, numBits ?? characters.Length);
        for (int i = 0; i < characters.Length; i++)
        {
            memory.Write(Player(i) + LabRandomFighter.GameplayPlayerDataCharacter, characters[i]);
        }

        return memory;
    }

    private static nint Player(int i) => (nint)(Elements + i * LabRandomFighter.PlayerElementSize + LabRandomFighter.PlayerElementValue);

    [Fact]
    public void ThePlayerWhoPickedRandomIsFound()
    {
        var memory = MapWith([Jason, Random], 0b11);
        Assert.Equal([Player(1)], LabRandomFighter.RandomPlayers(memory, (nint)Setup, (nint)Random));
    }

    [Fact]
    public void EveryRandomPlayerIsFoundAndTheFlagsCanLiveOnTheHeap()
    {
        var memory = MapWith([Random, Jason, Random], 0b111, heap: true);
        Assert.Equal([Player(0), Player(2)], LabRandomFighter.RandomPlayers(memory, (nint)Setup, (nint)Random));
    }

    [Fact]
    public void AFreeSlotIsSkippedWhateverItHolds()
    {
        var memory = MapWith([Random, Jason], 0b10);
        Assert.Empty(LabRandomFighter.RandomPlayers(memory, (nint)Setup, (nint)Random));
    }

    [Fact]
    public void NobodyPickedRandom()
    {
        var memory = MapWith([Jason, Jason], 0b11);
        Assert.Empty(LabRandomFighter.RandomPlayers(memory, (nint)Setup, (nint)Random));
    }

    [Theory]
    [InlineData(0, null, null)]                                   // empty
    [InlineData(LabRandomFighter.MaxPlayers + 1, 64, 64)]           // more players than any Lab
    [InlineData(-1, null, null)]                                  // negative
    [InlineData(2, 1, null)]                                      // count over capacity
    [InlineData(2, null, 1)]                                      // count over the flags' bits
    public void AMapThatDoesNotReadAsOneIsLeftAlone(int count, int? max, int? numBits)
    {
        var memory = MapWith([Random, Random], 0b11, count, max, numBits);
        Assert.Empty(LabRandomFighter.RandomPlayers(memory, (nint)Setup, (nint)Random));
    }

    [Fact]
    public void UnreadableElementsOrSetupAreLeftAlone()
    {
        var memory = MapWith([Random], 0b1);
        memory.Write(Map, 0x7777000L);
        Assert.Empty(LabRandomFighter.RandomPlayers(memory, (nint)Setup, (nint)Random));
        Assert.Empty(LabRandomFighter.RandomPlayers(new FakeMemory(), (nint)Setup, (nint)Random));
        Assert.Empty(LabRandomFighter.RandomPlayers(memory, 0, (nint)Random));
    }

    [Fact]
    public void WithoutTheRandomFighterNothingIsFound()
    {
        var memory = MapWith([0, Jason], 0b11);
        Assert.Empty(LabRandomFighter.RandomPlayers(memory, (nint)Setup, 0));
    }

    // The match config: a TArray<FGameplayPlayerData> at GameplayConfigPlayers.
    private const long Config = 0x40000;
    private const long ConfigPlayers = 0x50000;

    private static FakeMemory ConfigWith(long[] characters, int? count = null, int? max = null)
    {
        var memory = new FakeMemory();
        memory.Alloc((nint)Config, 0x200);
        memory.Alloc((nint)ConfigPlayers, Mvs.GameplayPlayerDataSize * Math.Max(characters.Length, 1));
        memory.Write((nint)(Config + Mvs.GameplayConfigPlayers), ConfigPlayers);
        memory.Write((nint)(Config + Mvs.GameplayConfigPlayers + 8), count ?? characters.Length);
        memory.Write((nint)(Config + Mvs.GameplayConfigPlayers + 12), max ?? characters.Length);
        for (int i = 0; i < characters.Length; i++)
        {
            memory.Write(ConfigPlayer(i) + LabRandomFighter.GameplayPlayerDataCharacter, characters[i]);
        }

        return memory;
    }

    private static nint ConfigPlayer(int i) => (nint)(ConfigPlayers + i * Mvs.GameplayPlayerDataSize);

    [Fact]
    public void TheMatchConfigsRandomPlayerIsFound()
    {
        var memory = ConfigWith([Random, Jason]);
        Assert.Equal([ConfigPlayer(0)], LabRandomFighter.ConfigRandomPlayers(memory, (nint)Config, (nint)Random));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-3, null)]
    [InlineData(LabRandomFighter.MaxPlayers + 1, 64)]
    [InlineData(2, 1)]
    public void AConfigArrayThatDoesNotReadAsOneIsLeftAlone(int count, int? max)
    {
        var memory = ConfigWith([Random, Random], count, max);
        Assert.Empty(LabRandomFighter.ConfigRandomPlayers(memory, (nint)Config, (nint)Random));
    }

    [Fact]
    public void AnUnreadableConfigOrNoRandomFighterFindsNothing()
    {
        var memory = ConfigWith([Random]);
        Assert.Empty(LabRandomFighter.ConfigRandomPlayers(memory, (nint)Config, 0));
        Assert.Empty(LabRandomFighter.ConfigRandomPlayers(memory, 0, (nint)Random));
        Assert.Empty(LabRandomFighter.ConfigRandomPlayers(new FakeMemory(), (nint)Config, (nint)Random));
        memory.Write((nint)(Config + Mvs.GameplayConfigPlayers), 0x7777000L);
        Assert.Empty(LabRandomFighter.ConfigRandomPlayers(memory, (nint)Config, (nint)Random));
    }
}
