namespace OpenVersus.Memory;

/// <summary>
/// Hooks a game function at its entry, for functions that are not reached through one call
/// site <see cref="CallSite.Redirect"/> could take. The first instructions (the prologue) are
/// checked against the bytes a disassembly of this build found there, copied into a gateway
/// that runs them and jumps back into the function, and replaced by a jmp to the hook. The
/// hook calls the gateway to run the original function.
/// <para>
/// There is no instruction decoder: the caller passes whole instructions, at least five bytes,
/// whose length it knows from the disassembly. They are copied as they are, so they must not
/// address anything relative to where they sit, with one exception: a rel32 conditional jump
/// (0F 80-8F) as the last instruction, which the gateway rewrites to reach the same target.
/// The gateway addresses nothing relative to itself, so it can sit anywhere.
/// </para>
/// </summary>
public static unsafe class EntryHook
{
    // jmp qword ptr [rip+0], followed by the 8-byte target: an absolute jump that works from anywhere.
    private static readonly byte[] s_absoluteJump = [0xFF, 0x25, 0x00, 0x00, 0x00, 0x00];
    private const int AbsoluteJumpLength = 14;
    private const int ConditionalJumpLength = 6;

    /// <summary>
    /// Hooks <paramref name="function"/> and returns the gateway, which the hook calls to run the
    /// original. <paramref name="prologue"/> is the whole instructions to move (at least five bytes);
    /// <paramref name="endsInConditionalJump"/> says the last six of them are a rel32 conditional
    /// jump. Throws a <see cref="PatchException"/> when the bytes there are not the prologue or a
    /// write fails; nothing is written to the function in the first case.
    /// </summary>
    public static nint Install(nint function, ReadOnlySpan<byte> prologue, nint hook, bool endsInConditionalJump = false)
    {
        CodeWriter.Expect(function, prologue);
        byte[] gateway = BuildGateway(function, prologue, endsInConditionalJump);
        nint gatewayAddress = Trampoline.Near(function).Place(gateway, align: 16);

        CallSite.Inject(function, hook, jump: true);
        if (prologue.Length > CallSite.Length)
        {
            // The rest of the moved instructions is never executed again; int3 makes a jump into it fail loudly.
            byte[] fill = new byte[prologue.Length - CallSite.Length];
            Array.Fill(fill, (byte)0xCC);
            CodeWriter.Write(function + CallSite.Length, fill, code: true);
        }

        return gatewayAddress;
    }

    /// <summary>
    /// The gateway for <paramref name="function"/>: the prologue, then an absolute jump to the
    /// instruction after it. A trailing rel32 conditional jump becomes the opposite short jump over
    /// an absolute jump to the original target. Throws a <see cref="PatchException"/> when the
    /// prologue is shorter than the jmp that replaces it, or its last six bytes are not a rel32
    /// conditional jump although <paramref name="endsInConditionalJump"/> says so.
    /// </summary>
    public static byte[] BuildGateway(nint function, ReadOnlySpan<byte> prologue, bool endsInConditionalJump)
    {
        if (prologue.Length < CallSite.Length)
        {
            throw new PatchException($"a prologue of {prologue.Length} bytes at 0x{function:X} is shorter than the {CallSite.Length}-byte jmp that replaces it");
        }

        var gateway = new List<byte>(prologue.Length + 2 * AbsoluteJumpLength + 2);
        int copied = prologue.Length;
        if (endsInConditionalJump)
        {
            copied -= ConditionalJumpLength;
            ReadOnlySpan<byte> jcc = copied >= 0 ? prologue[copied..] : default;
            if (jcc.Length != ConditionalJumpLength || jcc[0] != 0x0F || (jcc[1] & 0xF0) != 0x80)
            {
                throw new PatchException($"the prologue at 0x{function:X} does not end in a rel32 conditional jump");
            }

            nint target = function + prologue.Length + BitConverter.ToInt32(jcc[2..]);
            gateway.AddRange(prologue[..copied].ToArray());
            // The opposite condition (the low bit flips it) skips the jump taken when the original's holds.
            gateway.Add((byte)(0x70 | ((jcc[1] & 0x0F) ^ 1)));
            gateway.Add(AbsoluteJumpLength);
            AddAbsoluteJump(gateway, target);
        }
        else
        {
            gateway.AddRange(prologue.ToArray());
        }

        AddAbsoluteJump(gateway, function + prologue.Length);
        return gateway.ToArray();
    }

    private static void AddAbsoluteJump(List<byte> code, nint target)
    {
        code.AddRange(s_absoluteJump);
        code.AddRange(BitConverter.GetBytes((long)target));
    }
}
