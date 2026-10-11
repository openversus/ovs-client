namespace OpenVersus.Memory;

/// <summary>
/// Five-byte rel32 call and jmp instructions in the game's code: reading where they go and
/// pointing them somewhere else. The C++ client's MakeProxyFromOpCode, GetProcFromOpCode and
/// InjectHook, with the same arithmetic.
/// </summary>
public static unsafe class CallSite
{
    /// <summary>The opcode of a rel32 call.</summary>
    public const byte CallOpcode = 0xE8;
    /// <summary>The opcode of a rel32 jmp.</summary>
    public const byte JumpOpcode = 0xE9;
    /// <summary>The length of a rel32 call or jmp.</summary>
    public const int Length = 5;

    /// <summary>The 32-bit displacement at <paramref name="displacementOffset"/> into the instruction.</summary>
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
    /// Throws a <see cref="PatchException"/> if the byte there is not E8 or E9, since this is only
    /// meaningful on a rel32 instruction.
    /// </summary>
    public static nint Redirect(nint instruction, nint target)
    {
        byte opcode = *(byte*)instruction;
        if (opcode is not (CallOpcode or JumpOpcode))
        {
            throw new PatchException($"expected a call or jmp at 0x{instruction:X}, found opcode {opcode:X2}");
        }

        nint original = Destination(instruction);
        Inject(instruction, target, opcode == JumpOpcode);
        return original;
    }

    /// <summary>
    /// Overwrites the five bytes at <paramref name="instruction"/> with a call (or jmp) to
    /// <paramref name="target"/> through a near stub, whatever was there before, and returns the
    /// bytes that were there, for the log. Throws a <see cref="PatchException"/> on failure.
    /// </summary>
    public static byte[] Inject(nint instruction, nint target, bool jump)
    {
        byte[] overwritten = CodeWriter.Read(instruction, Length);
        nint stub = Trampoline.Near(instruction).Jump(target);
        long displacement = (long)stub - (long)(instruction + Length);
        if (displacement < int.MinValue || displacement > int.MaxValue)
        {
            throw new PatchException($"stub at 0x{stub:X} is out of rel32 range of 0x{instruction:X}");
        }

        Span<byte> bytes = stackalloc byte[Length];
        bytes[0] = jump ? JumpOpcode : CallOpcode;
        BitConverter.TryWriteBytes(bytes[1..], (int)displacement);
        CodeWriter.Write(instruction, bytes, code: true);
        return overwritten;
    }

    /// <summary>
    /// <see cref="Inject"/> through a stub that runs <paramref name="prelude"/> before jumping to
    /// <paramref name="target"/>, for a hook that needs a value the game keeps in a register at
    /// that call (the prelude copies it into a free argument register, e.g. mov r8, r15). The
    /// prelude is copied as it is, so it must not address anything relative to where it sits.
    /// Returns the bytes that were overwritten. Throws a <see cref="PatchException"/> on failure.
    /// </summary>
    public static byte[] InjectWithPrelude(nint instruction, ReadOnlySpan<byte> prelude, nint target, bool jump)
    {
        nint stub = Trampoline.Near(instruction).Place(BuildPrelude(prelude, target), align: 16);
        return Inject(instruction, stub, jump);
    }

    /// <summary>The <see cref="InjectWithPrelude"/> stub: the prelude, then an absolute jump to <paramref name="target"/>.</summary>
    public static byte[] BuildPrelude(ReadOnlySpan<byte> prelude, nint target)
    {
        var code = new List<byte>(prelude.Length + 14);
        code.AddRange(prelude);
        EntryHook.AddAbsoluteJump(code, target);
        return code.ToArray();
    }
}
