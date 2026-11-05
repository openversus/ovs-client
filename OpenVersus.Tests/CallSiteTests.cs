using OpenVersus.Native;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

public unsafe class CallSiteTests
{
    [Fact]
    public void DestinationOfACallIsNextInstructionPlusDisplacement()
    {
        // call +0x10 : E8 10 00 00 00
        byte[] code = [0xE8, 0x10, 0x00, 0x00, 0x00, 0x90];
        fixed (byte* p = code)
        {
            nint at = (nint)p;
            Assert.Equal(at + 5 + 0x10, CallSite.Destination(at));
        }
    }

    [Fact]
    public void NegativeDisplacementAndOtherInstructionShapes()
    {
        // jne -6 : 0F 85 FA FF FF FF  (two opcode bytes, six long)
        byte[] code = [0x0F, 0x85, 0xFA, 0xFF, 0xFF, 0xFF];
        fixed (byte* p = code)
        {
            nint at = (nint)p;
            Assert.Equal(at + 6 - 6, CallSite.Destination(at, displacementOffset: 2, instructionLength: 6));
        }
        // lea rcx, [rip+0x1234] : 48 8D 0D 34 12 00 00  (three opcode bytes, seven long)
        byte[] lea = [0x48, 0x8D, 0x0D, 0x34, 0x12, 0x00, 0x00];
        fixed (byte* p = lea)
        {
            nint at = (nint)p;
            Assert.Equal(at + 7 + 0x1234, CallSite.Destination(at, displacementOffset: 3, instructionLength: 7));
        }
    }

    [Fact]
    public void APreludeStubIsThePreludeThenAnAbsoluteJump()
    {
        const long target = 0x1122334455667788;
        byte[] stub = CallSite.BuildPrelude([0x4D, 0x89, 0xF8], unchecked((nint)target));

        Assert.Equal(new byte[] { 0x4D, 0x89, 0xF8, 0xFF, 0x25, 0, 0, 0, 0 }, stub[..9]);
        Assert.Equal(target, BitConverter.ToInt64(stub, 9));
        Assert.Equal(3 + 14, stub.Length);
    }

    [SkippableFact]
    public void ThePreludeRunsBeforeTheTarget()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "runs generated x64 code");
        nint memory = Kernel32.VirtualAlloc(0, 4096, Kernel32.MEM_COMMIT | Kernel32.MEM_RESERVE, Kernel32.PAGE_EXECUTE_READWRITE);
        Assert.NotEqual(0, memory);
        try
        {
            // The target returns its third argument (mov rax, r8; ret); the prelude copies the first into it (mov r8, rcx).
            byte[] returnsR8 = [0x4C, 0x89, 0xC0, 0xC3];
            returnsR8.CopyTo(new Span<byte>((void*)memory, returnsR8.Length));
            byte[] stub = CallSite.BuildPrelude([0x49, 0x89, 0xC8], memory);
            stub.CopyTo(new Span<byte>((void*)(memory + 64), stub.Length));
            var call = (delegate* unmanaged<nint, nint, nint, nint>)(memory + 64);

            Assert.Equal(0x1234, call(0x1234, 0, 0x999));
        }
        finally
        {
            Kernel32.VirtualFree(memory, 0, Kernel32.MEM_RELEASE);
        }
    }
}
