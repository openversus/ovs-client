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

    /// <summary>Where the watched value sits in a <see cref="BuildFilter"/> stub.</summary>
    public const int FilterSlotOffset = 40;

    /// <summary>
    /// <see cref="Install"/> for a function called far too often to enter the hook on every call
    /// (UObject::ProcessEvent), when only calls with one second argument matter. The function
    /// jumps to a filter that compares its second argument (rdx) with a watched value and goes
    /// straight to the gateway unless they are equal, so every other call costs a load, a compare
    /// and two jumps and never reaches managed code. The watched value starts at 0, which no real
    /// argument is, and is changed with <see cref="SetWatched"/> at <paramref name="watchSlot"/>.
    /// Returns the gateway. Throws a <see cref="PatchException"/> as <see cref="Install"/> does.
    /// </summary>
    public static nint InstallFiltered(nint function, ReadOnlySpan<byte> prologue, nint hook, out nint watchSlot)
    {
        CodeWriter.Expect(function, prologue);
        var trampoline = Trampoline.Near(function);
        nint gatewayAddress = trampoline.Place(BuildGateway(function, prologue, endsInConditionalJump: false), align: 16);
        nint filterAddress = trampoline.Place(BuildFilter(hook, gatewayAddress), align: 16);
        watchSlot = filterAddress + FilterSlotOffset;

        CallSite.Inject(function, filterAddress, jump: true);
        if (prologue.Length > CallSite.Length)
        {
            byte[] fill = new byte[prologue.Length - CallSite.Length];
            Array.Fill(fill, (byte)0xCC);
            CodeWriter.Write(function + CallSite.Length, fill, code: true);
        }

        return gatewayAddress;
    }

    /// <summary>Sets the value an <see cref="InstallFiltered"/> filter sends to the hook; 0 sends nothing.</summary>
    public static void SetWatched(nint watchSlot, nint value) => CodeWriter.Write(watchSlot, BitConverter.GetBytes((long)value), code: true);

    /// <summary>
    /// The filter stub: rax is free at a function's entry (the caller keeps nothing in it), so
    /// <c>mov rax, [watched]; cmp rdx, rax; jne gateway; jmp hook</c>, with both jumps absolute and
    /// the watched value, 0 at first, at <see cref="FilterSlotOffset"/>.
    /// </summary>
    public static byte[] BuildFilter(nint hook, nint gateway)
    {
        var code = new List<byte>(FilterSlotOffset + 8);
        code.AddRange([0x48, 0x8B, 0x05]); // mov rax, [rip+disp32]
        code.AddRange(BitConverter.GetBytes(FilterSlotOffset - 7));
        code.AddRange([0x48, 0x39, 0xC2]); // cmp rdx, rax
        code.AddRange([0x75, AbsoluteJumpLength]); // jne over the jump to the hook
        AddAbsoluteJump(code, hook);
        AddAbsoluteJump(code, gateway);
        code.AddRange(new byte[8]);
        return code.ToArray();
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
