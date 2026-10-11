using OpenVersus.Memory;
using OpenVersus.Native;

namespace OpenVersus.Tests;

public unsafe class CodeWriterTests
{
    [SkippableFact]
    public void TryWriteWritesAValueAndReadsBackTheSame()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WriteProcessMemory");
        byte[] buffer = new byte[16];
        fixed (byte* p = buffer)
        {
            Assert.True(CodeWriter.TryWrite((nint)(p + 4), 0x11223344));
            Assert.True(CodeWriter.TryWrite((nint)(p + 8), (byte)1));
            Assert.True(CodeWriter.TryRead((nint)(p + 4), out int read));
            Assert.Equal(0x11223344, read);
        }

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x44, 0x33, 0x22, 0x11, 1, 0, 0, 0, 0, 0, 0, 0 }, buffer);
    }

    [SkippableFact]
    public void TryWriteToMemoryThatIsNotThereIsAFalseNotACrash()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WriteProcessMemory");
        // Reserved, never committed: no page is there, and nothing else can be given the range while it is held
        // (a freed range could be, by a test running alongside, and a write that then succeeds would be into its memory).
        nint memory = Kernel32.VirtualAlloc(0, 4096, Kernel32.MEM_RESERVE, Kernel32.PAGE_READWRITE);
        Assert.NotEqual(0, memory);
        try
        {
            Assert.False(CodeWriter.TryWrite(memory, 1));
            Assert.False(CodeWriter.TryWrite(16, (byte)1));
        }
        finally
        {
            Kernel32.VirtualFree(memory, 0, Kernel32.MEM_RELEASE);
        }
    }
}
