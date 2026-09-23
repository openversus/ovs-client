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
}
