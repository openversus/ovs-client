using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="PvPBotsPatch"/> against the real bytes of the 1v1/2v2 gate in the bot timer's setup, copied
/// from the game's only build (0x1425e2dde to 0x1425e2e00): the pattern finds the gate, the value pointer it
/// reads is PFG.PvPBots's (0x1480aa908, the global its registration writes), and a zero skips the timer.
/// Writing the value is proven in the game.
/// </summary>
public unsafe class PvPBotsPatchTests
{
    private const long Start = 0x1425e2dde;
    private const long PvPBotsValuePointer = 0x1480aa908;
    private const long NoTimer = 0x1425e3007;

    private static readonly byte[] s_gate = Convert.FromHexString((
        "0F 94 C0 84 C0 75 0A 40 84 F6 75 15 40 84 FF EB 0A 48 8B 05 12 7B AC 05 " +
        "83 38 00 0F 84 08 02 00 00").Replace(" ", ""));

    private static int Match() => PatternScanner.FindFirst(s_gate, BytePattern.Parse(PvPBotsPatch.Pattern));

    [Fact]
    public void ThePatternFindsTheGate()
    {
        Assert.Equal(0x1425e2dea - Start, Match());
    }

    [Fact]
    public void TheSlotIsPvPBotsValuePointer()
    {
        int m = Match();
        fixed (byte* p = s_gate)
        {
            nint slot = CallSite.Destination((nint)(p + m + PvPBotsPatch.LoadOffset), displacementOffset: 3, instructionLength: 7);
            Assert.Equal(PvPBotsValuePointer - Start, slot - (nint)p);
        }
    }

    [Fact]
    public void AZeroSkipsTheTimer()
    {
        // cmp dword [rax], 0; je rel32: equal to zero goes to the path that arms nothing.
        int je = Match() + PvPBotsPatch.LoadOffset + 7 + 3;
        Assert.Equal([(byte)0x0F, (byte)0x84], s_gate[je..(je + 2)]);
        Assert.Equal(NoTimer - Start, je + 6 + BitConverter.ToInt32(s_gate, je + 2));
    }
}
