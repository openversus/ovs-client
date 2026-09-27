using OpenVersus.Memory;

namespace OpenVersus.Tests;

public class EntryHookTests
{
    private const long FunctionAddress = 0x1_4296_6870;
    private static readonly nint Function = unchecked((nint)FunctionAddress);

    [Fact]
    public void TheGatewayRunsThePrologueThenJumpsBackAfterIt()
    {
        // push rsi; sub rsp, 0x50 (AttemptEndMatch)
        byte[] prologue = [0x40, 0x56, 0x48, 0x83, 0xEC, 0x50];
        byte[] gateway = EntryHook.BuildGateway(Function, prologue, endsInConditionalJump: false);

        Assert.Equal(prologue, gateway[..6]);
        // jmp qword ptr [rip+0] to function + 6
        Assert.Equal(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }, gateway[6..12]);
        Assert.Equal(FunctionAddress + 6, BitConverter.ToInt64(gateway, 12));
        Assert.Equal(20, gateway.Length);
    }

    [Fact]
    public void ATrailingConditionalJumpStillReachesItsTarget()
    {
        // test rdx, rdx; je +0x59A (PlayerDied): the je's target is function + 9 + 0x59A.
        byte[] prologue = [0x48, 0x85, 0xD2, 0x0F, 0x84, 0x9A, 0x05, 0x00, 0x00];
        byte[] gateway = EntryHook.BuildGateway(Function, prologue, endsInConditionalJump: true);

        Assert.Equal(prologue[..3], gateway[..3]);
        // jne (the opposite of je) over the 14-byte absolute jump to the je's target
        Assert.Equal(0x75, gateway[3]);
        Assert.Equal(14, gateway[4]);
        Assert.Equal(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }, gateway[5..11]);
        Assert.Equal(FunctionAddress + 9 + 0x59A, BitConverter.ToInt64(gateway, 11));
        // then back into the function after the moved instructions
        Assert.Equal(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }, gateway[19..25]);
        Assert.Equal(FunctionAddress + 9, BitConverter.ToInt64(gateway, 25));
    }

    [Fact]
    public void AShortPrologueOrAMissingConditionalJumpIsRefused()
    {
        Assert.Throws<PatchException>(() => EntryHook.BuildGateway(Function, new byte[] { 0x90, 0x90, 0x90, 0x90 }, false));
        Assert.Throws<PatchException>(() => EntryHook.BuildGateway(Function, new byte[] { 0x40, 0x56, 0x48, 0x83, 0xEC, 0x50 }, true));
        Assert.Throws<PatchException>(() => EntryHook.BuildGateway(Function, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 }, true));
    }
}
