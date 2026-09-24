namespace OpenVersus.Memory;

/// <summary>
/// Five-byte rel32 call and jmp instructions in the game's code: reading where they go and
/// pointing them somewhere else. The C++ client's MakeProxyFromOpCode, GetProcFromOpCode and
/// InjectHook, with the same arithmetic.
/// </summary>
public static unsafe class CallSite
{
    public const byte CallOpcode = 0xE8;
    public const byte JumpOpcode = 0xE9;
    public const int Length = 5;

    public static int ReadRel32(nint instruction, int displacementOffset = 1) => *(int*)(instruction + displacementOffset);

    /// <summary>
    /// Where a rel32 instruction goes: instruction + its length + displacement. The defaults are
    /// a call or jmp (one opcode byte, five bytes long); a conditional near jump is (2, 6), and
    /// a RIP-relative lea is (3, 7).
    /// </summary>
    public static nint Destination(nint instruction, int displacementOffset = 1, int instructionLength = Length) =>
        instruction + instructionLength + ReadRel32(instruction, displacementOffset);

    /// <summary>
    /// Points the call or jmp at <paramref name="instruction"/> at <paramref name="target"/>
    /// through a near stub, and returns where it used to go, so the hook can call the original.
    /// Refuses if the byte there is not E8 or E9: this is only meaningful on a rel32 instruction.
    /// </summary>
    public static nint Redirect(nint instruction, nint target, out string? error)
    {
        byte opcode = *(byte*)instruction;
        if (opcode is not (CallOpcode or JumpOpcode))
        {
            error = $"expected a call or jmp at 0x{instruction:X}, found opcode {opcode:X2}";
            return 0;
        }
        nint original = Destination(instruction);
        error = Inject(instruction, target, opcode == JumpOpcode, out _);
        return error == null ? original : 0;
    }

    /// <summary>
    /// Overwrites the five bytes at <paramref name="instruction"/> with a call (or jmp) to
    /// <paramref name="target"/> through a near stub, whatever was there before. The bytes that
    /// were there come back in <paramref name="overwritten"/> for the log. Null on success.
    /// </summary>
    public static string? Inject(nint instruction, nint target, bool jump, out byte[] overwritten)
    {
        overwritten = CodeWriter.Read(instruction, Length);
        nint stub = Trampoline.Near(instruction).Jump(target);
        long displacement = (long)stub - (long)(instruction + Length);
        if (displacement < int.MinValue || displacement > int.MaxValue)
        {
            return $"stub at 0x{stub:X} is out of rel32 range of 0x{instruction:X}";
        }

        Span<byte> bytes = stackalloc byte[Length];
        bytes[0] = jump ? JumpOpcode : CallOpcode;
        BitConverter.TryWriteBytes(bytes[1..], (int)displacement);
        return CodeWriter.Write(instruction, bytes, code: true);
    }
}
