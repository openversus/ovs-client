using OpenVersus.Memory;
using OpenVersus.Native;

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

    [Fact]
    public void TheFilterLoadsTheWatchedValueAndJumpsToTheHookOrTheGateway()
    {
        const long hook = 0x7FF6_0000_1000, gateway = 0x7FF6_0000_2000;
        byte[] filter = EntryHook.BuildFilter(unchecked((nint)hook), unchecked((nint)gateway));

        // mov rax, [rip+33]: the slot, 40 bytes in, from the end of the 7-byte mov
        Assert.Equal(new byte[] { 0x48, 0x8B, 0x05, 0x21, 0, 0, 0 }, filter[..7]);
        Assert.Equal(new byte[] { 0x48, 0x39, 0xC2, 0x75, 14 }, filter[7..12]);
        Assert.Equal(hook, BitConverter.ToInt64(filter, 18));
        Assert.Equal(gateway, BitConverter.ToInt64(filter, 32));
        Assert.Equal(EntryHook.FilterSlotOffset + 8, filter.Length);
        Assert.Equal(0, BitConverter.ToInt64(filter, EntryHook.FilterSlotOffset));
    }

    [SkippableFact]
    public unsafe void OnlyTheWatchedSecondArgumentReachesTheHook()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "runs generated x64 code");
        nint memory = Kernel32.VirtualAlloc(0, 4096, Kernel32.MEM_COMMIT | Kernel32.MEM_RESERVE, Kernel32.PAGE_EXECUTE_READWRITE);
        Assert.NotEqual(0, memory);
        try
        {
            // The "hook" returns 1 and the "gateway" 2: mov eax, n; ret
            byte[] returnsOne = [0xB8, 1, 0, 0, 0, 0xC3], returnsTwo = [0xB8, 2, 0, 0, 0, 0xC3];
            returnsOne.CopyTo(new Span<byte>((void*)memory, 6));
            returnsTwo.CopyTo(new Span<byte>((void*)(memory + 16), 6));
            byte[] filter = EntryHook.BuildFilter(memory, memory + 16);
            filter.CopyTo(new Span<byte>((void*)(memory + 64), filter.Length));
            var call = (delegate* unmanaged<nint, nint, int>)(memory + 64);
            nint slot = memory + 64 + EntryHook.FilterSlotOffset;

            Assert.Equal(2, call(0, 0x1234));
            EntryHook.SetWatched(slot, 0x1234);
            Assert.Equal(1, call(0, 0x1234));
            Assert.Equal(2, call(0x1234, 0x999));
            EntryHook.SetWatched(slot, 0);
            Assert.Equal(2, call(0, 0x1234));
        }
        finally
        {
            Kernel32.VirtualFree(memory, 0, Kernel32.MEM_RELEASE);
        }
    }
}
