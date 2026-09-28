using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="TelemetryPatch"/> against the real bytes of WBA's send function, copied from the game's
/// only build (0x140bea5a7 to 0x140bea89e): the pattern finds it, both byte runs the patch checks are
/// where it expects, the jump it writes goes where the je went, and the HTTP request is created only
/// on the path the jump skips. Writing the bytes needs VirtualProtect, so that part is proven in the game.
/// </summary>
public class TelemetryPatchTests
{
    private static readonly byte[] s_function = Convert.FromHexString((
        "45 33 FF 48 8B F1 48 8B 09 4C 89 7D D7 4C 89 7D DF E8 C3 18 00 00 84 C0 " +
        "0F 84 AA 02 00 00 65 48 8B 04 25 58 00 00 00 8B 0D 1C 43 7B 07 48 89 9C " +
        "24 E0 00 00 00 BA 1C 08 00 00 4C 89 B4 24 C8 00 00 00 48 8B 1C C8 48 03 " +
        "DA 8B 03 39 05 B8 5F 42 07 0F 8F E9 02 00 00 8B 03 39 05 C2 5F 42 07 0F " +
        "8F 24 03 00 00 8B 03 39 05 CC 5F 42 07 0F 8F 84 02 00 00 E8 31 20 07 03 " +
        "48 8D 55 B7 48 8B C8 4C 8B 08 41 FF 51 40 48 8B 4D B7 48 8D 15 98 5F 42 " +
        "07 48 8B 01 FF 50 48 48 8B 4D B7 4C 8D 05 6F 5F 42 07 48 8D 15 50 5F 42 " +
        "07 48 8B 01 FF 90 80 00 00 00 48 8B 4D B7 48 8B 16 48 8B 01 48 8B 52 18 " +
        "FF 50 50 48 8B 4D B7 48 8D 55 D7 48 8B 01 FF 50 60 48 8B 4D B7 48 8B 01 " +
        "FF 90 B0 00 00 00 8B 5E 10 4C 8B F0 48 8B 7E 08 4C 89 7D C7 89 5D CF 85 " +
        "DB 75 06 44 89 7D D3 EB 42 45 33 C0 48 8D 4D C7 8B D3 E8 12 2E F9 FF 48 " +
        "8B 4D C7 48 2B F9 66 66 66 0F 1F 84 00 00 00 00 00 48 8B 04 0F 48 89 01 " +
        "48 8B 44 0F 08 48 89 41 08 48 85 C0 74 04 F0 FF 40 08 48 83 C1 10 83 EB " +
        "01 75 DE 48 8B 16 48 8D 45 C7 4C 8D 4D E7 48 89 44 24 20 4C 8D 05 67 6D " +
        "00 00 4C 89 7D E7 49 8B CE 4C 89 7D EF E8 17 D3 FF FF 48 8B 4D B7 48 8B " +
        "01 FF 90 A8 00 00 00 4C 8B B4 24 C8 00 00 00 84 C0 0F 84 83 00 00 00 B9 " +
        "06 00 00 00 E8 F0 01 F8 FF 84 C0 0F 84 F8 00 00 00 4C 8B 06 45 39 B8 98 " +
        "00 00 00 74 09 4D 8B 80 90 00 00 00 EB 07 4C 8D 05 CC 17 0D 05 48 8D 15 " +
        "95 B4 0E 05 48 8D 4D 07 E8 CC E8 EA 01 48 8D 15 15 18 0D 05 48 8D 4D F7 " +
        "E8 EC B6 EA 01 48 8D 45 07 41 B9 06 00 00 00 4C 8D 45 F7 48 89 44 24 20 " +
        "33 D2 48 8D 0D F0 17 0D 05 E8 6B 01 F8 FF 48 8B 4D F7 48 85 C9 74 05 E8 " +
        "6D 16 EE 01 48 8B 4D 07 EB 7D B9 02 00 00 00 E8 6D 01 F8 FF 84 C0 74 79 " +
        "4C 8B 06 45 39 B8 98 00 00 00 74 09 4D 8B 80 90 00 00 00 EB 07 4C 8D 05 " +
        "4D 17 0D 05 48 8D 15 4E B4 0E 05 48 8D 4D 27 E8 4D E8 EA 01 48 8D 15 96 " +
        "17 0D 05 48 8D 4D 17 E8 6D B6 EA 01 48 8D 45 27 41 B9 02 00 00 00 4C 8D " +
        "45 17 48 89 44 24 20 33 D2 48 8D 0D 71 17 0D 05 E8 EC 00 F8 FF 48 8B 4D " +
        "17 48 85 C9 74 05 E8 EE 15 EE 01 48 8B 4D 27 48 85 C9 74 05 E8 E0 15 EE " +
        "01 48 8B 5D BF 48 85 DB 74 2E BF FF FF FF FF 8B C7 F0 0F C1 43 08 83 F8 " +
        "01 75 1D 48 8B 03 48 8B CB FF 10 F0 0F C1 7B 0C 83 FF 01 75 0B 48 8B 03 " +
        "8B D7 48 8B CB FF 50 08 48 8B 9C 24 E0 00 00 00 48 8B 4D D7 4C 8B BC 24 " +
        "C0 00 00 00 48 8B BC 24 F0 00 00 00 48 8B B4 24 E8 00 00 00 48 85 C9 74 " +
        "05 E8 7B 15 EE 01 48 81 C4 D0 00 00 00 5D C3").Replace(" ", ""));

    private static int Match() => PatternScanner.FindFirst(s_function, BytePattern.Parse(TelemetryPatch.Pattern));

    [Fact]
    public void ThePatternFindsTheNoSendBranch()
    {
        Assert.Equal(0, Match());
    }

    [Fact]
    public void TheJeAndTheNoSendReturnAreWhereThePatchExpects()
    {
        int m = Match();
        Assert.Equal(TelemetryPatch.Expected, s_function[(m + TelemetryPatch.BranchOffset)..(m + TelemetryPatch.BranchOffset + TelemetryPatch.Expected.Length)]);
        Assert.Equal(TelemetryPatch.Exit, s_function[(m + TelemetryPatch.ExitOffset)..(m + TelemetryPatch.ExitOffset + TelemetryPatch.Exit.Length)]);
        // The return ends in pop rbp; ret.
        Assert.Equal([0x5D, 0xC3], s_function[^2..]);
    }

    [Fact]
    public void TheJumpGoesWhereTheJeWentAlways()
    {
        int branch = Match() + TelemetryPatch.BranchOffset;
        int jeTarget = branch + 6 + BitConverter.ToInt32(s_function, branch + 2);
        Assert.Equal(Match() + TelemetryPatch.ExitOffset, jeTarget);

        Assert.Equal(TelemetryPatch.Expected.Length, TelemetryPatch.Jump.Length);
        Assert.Equal(0xE9, TelemetryPatch.Jump[0]);
        Assert.Equal(jeTarget, branch + 5 + BitConverter.ToInt32(TelemetryPatch.Jump, 1));
        Assert.Equal(0x90, TelemetryPatch.Jump[5]);
    }

    [Fact]
    public void TheRequestIsOnlyCreatedOnThePathTheJumpSkips()
    {
        // call qword [r9 + 0x40]: IHttpModule-style CreateRequest through the HTTP module's vtable.
        int m = Match();
        int create = s_function.AsSpan().IndexOf([(byte)0x41, (byte)0xFF, (byte)0x51, (byte)0x40]);
        Assert.InRange(create, m + TelemetryPatch.BranchOffset + 6, m + TelemetryPatch.ExitOffset - 1);
    }

    // UFighterGameInstance::RecordEventWithAttributes, 0x142729010 to 0x142729137 in the game's only build.
    private const long RecordAddress = 0x142729010;
    private const long ArrayDestructor = 0x140cc60f0;
    private static readonly byte[] s_record = Convert.FromHexString((
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 " +
        "EC 40 49 8B E8 4C 8B F2 48 8B F1 E8 28 D0 B9 FF 48 8B D8 48 85 C0 74 24 " +
        "E8 1B D0 B9 FF 48 85 C0 74 1A 48 8D 50 40 48 63 40 48 3B 43 48 7F 0D 48 " +
        "8B C8 48 8B 43 40 48 39 14 C8 74 02 33 DB 48 8B D3 48 8D 8E 10 01 00 00 " +
        "E8 BB 67 7C 02 48 8B F8 48 85 C0 0F 84 94 00 00 00 48 8B CE E8 87 26 00 " +
        "00 48 8B 47 48 48 8D 15 8C 2E 59 03 48 8D 4C 24 30 48 8B 58 40 E8 FE D0 " +
        "36 00 48 8B D5 48 8D 4C 24 20 E8 41 6F FF FF 4C 8D 4C 24 30 4C 8B C0 49 " +
        "8B D6 48 8D 4F 48 FF D3 8B 7C 24 28 48 8B 5C 24 20 85 FF 74 2C 0F 1F 00 " +
        "48 8B 4B 10 48 85 C9 74 05 E8 32 2D 3A 00 48 8B 0B 48 85 C9 74 05 E8 25 " +
        "2D 3A 00 48 83 C3 30 83 EF 01 75 DC 48 8B 5C 24 20 48 85 DB 74 08 48 8B " +
        "CB E8 0A 2D 3A 00 48 8B 4C 24 30 48 85 C9 74 05 E8 FB 2C 3A 00 48 8B CD " +
        "48 8B 5C 24 50 48 8B 6C 24 58 48 8B 74 24 60 48 8B 7C 24 68 48 83 C4 40 " +
        "41 5E E9 B9 CF 59 FE").Replace(" ", ""));

    private static int RecordMatch() => PatternScanner.FindFirst(s_record, BytePattern.Parse(TelemetryPatch.RecordPattern));

    [Fact]
    public void TheRecordPatternFindsTheFunctionStart()
    {
        Assert.Equal(0, RecordMatch());
    }

    [Fact]
    public void TheRecordJeAndTheFreeAndReturnPathAreWhereThePatchExpects()
    {
        int m = RecordMatch();
        Assert.Equal(TelemetryPatch.RecordExpected, s_record[(m + TelemetryPatch.RecordBranchOffset)..(m + TelemetryPatch.RecordBranchOffset + TelemetryPatch.RecordExpected.Length)]);
        Assert.Equal(TelemetryPatch.RecordExit, s_record[(m + TelemetryPatch.RecordExitOffset)..(m + TelemetryPatch.RecordExitOffset + TelemetryPatch.RecordExit.Length)]);
    }

    [Fact]
    public void TheFreeAndReturnPathOnlyTailJumpsToTheAttributesDestructor()
    {
        int exit = RecordMatch() + TelemetryPatch.RecordExitOffset;
        int jmp = s_record.Length - 5;
        Assert.Equal(0xE9, s_record[jmp]);
        Assert.Equal(ArrayDestructor, RecordAddress + s_record.Length + BitConverter.ToInt32(s_record, jmp + 1));
        // Nothing is called on the way: the locals the recording path builds are never freed there.
        Assert.DoesNotContain((byte)0xE8, s_record[exit..jmp]);
        // It hands the destructor rbp, the Attributes pointer the function saved from r8 on entry.
        Assert.Equal([0x49, 0x8B, 0xE8], s_record[0x1A..0x1D]);
        Assert.Equal([0x48, 0x8B, 0xCD], s_record[exit..(exit + 3)]);
    }

    [Fact]
    public void TheRecordJumpGoesWhereTheJeWentAndSkipsTheRecordCall()
    {
        int branch = RecordMatch() + TelemetryPatch.RecordBranchOffset;
        int jeTarget = branch + 6 + BitConverter.ToInt32(s_record, branch + 2);
        Assert.Equal(RecordMatch() + TelemetryPatch.RecordExitOffset, jeTarget);
        Assert.Equal(TelemetryPatch.RecordExpected.Length, TelemetryPatch.RecordJump.Length);
        Assert.Equal(0xE9, TelemetryPatch.RecordJump[0]);
        Assert.Equal(jeTarget, branch + 5 + BitConverter.ToInt32(TelemetryPatch.RecordJump, 1));
        Assert.Equal(0x90, TelemetryPatch.RecordJump[5]);
        // call rbx: the call that records the event, only on the path the jump skips.
        int record = s_record.AsSpan().IndexOf([(byte)0xFF, (byte)0xD3]);
        Assert.InRange(record, branch + 6, jeTarget - 1);
    }
}
