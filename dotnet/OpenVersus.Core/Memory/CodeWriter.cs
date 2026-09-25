using System.Runtime.InteropServices;
using OpenVersus.Native;

namespace OpenVersus.Memory;

/// <summary>Writes into the mapped image and checks what is actually there afterwards.</summary>
public static unsafe class CodeWriter
{
    /// <summary>
    /// Writes <paramref name="bytes"/> at <paramref name="target"/>, or throws a <see cref="PatchException"/>.
    /// Code pages stay executable while written: another thread may be running them, and a page
    /// flipped to read-write faults under it. The write is read back, so a return means the bytes
    /// are in memory, not that the call returned.
    /// </summary>
    public static void Write(nint target, ReadOnlySpan<byte> bytes, bool code)
    {
        uint protect = code ? Kernel32.PAGE_EXECUTE_READWRITE : Kernel32.PAGE_READWRITE;
        if (!Kernel32.VirtualProtect(target, (nuint)bytes.Length, protect, out uint oldProtect))
        {
            throw new PatchException($"VirtualProtect error {Marshal.GetLastPInvokeError()} at 0x{target:X}");
        }

        bytes.CopyTo(new Span<byte>((void*)target, bytes.Length));
        if (code)
        {
            Kernel32.FlushInstructionCache(Kernel32.GetCurrentProcess(), target, (nuint)bytes.Length);
        }

        if (!Kernel32.VirtualProtect(target, (nuint)bytes.Length, oldProtect, out _))
        {
            throw new PatchException($"written, but protection not restored (VirtualProtect error {Marshal.GetLastPInvokeError()}) at 0x{target:X}");
        }

        if (!new ReadOnlySpan<byte>((void*)target, bytes.Length).SequenceEqual(bytes))
        {
            throw new PatchException($"read back {Convert.ToHexString(Read(target, bytes.Length))} at 0x{target:X}, wrote {Convert.ToHexString(bytes)}");
        }
    }

    /// <summary>Writes only if the bytes there are <paramref name="expected"/>; throws a <see cref="PatchException"/> otherwise.</summary>
    public static void WriteIf(nint target, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> bytes, bool code)
    {
        Expect(target, expected);
        Write(target, bytes, code);
    }

    /// <summary>Throws a <see cref="PatchException"/> unless the bytes at <paramref name="target"/> are <paramref name="expected"/>.</summary>
    public static void Expect(nint target, ReadOnlySpan<byte> expected)
    {
        var current = new ReadOnlySpan<byte>((void*)target, expected.Length);
        if (!current.SequenceEqual(expected))
        {
            throw new PatchException($"expected {Convert.ToHexString(expected)} at 0x{target:X}, found {Convert.ToHexString(current)}; not patching");
        }
    }

    /// <summary>An unguarded copy of <paramref name="length"/> bytes at <paramref name="address"/>, for memory known to be mapped, such as the image.</summary>
    public static byte[] Read(nint address, int length) => new ReadOnlySpan<byte>((void*)address, length).ToArray();

    /// <summary>
    /// A read that cannot take the process down: <see cref="ProcessMemory"/>, for code that has
    /// no <see cref="IMemory"/> to hand (hooks and the game-thread jobs). Never throws.
    /// </summary>
    public static bool TryRead(nint address, Span<byte> into) => ProcessMemory.Instance.TryRead(address, into);

    /// <summary>One unmanaged value through <see cref="ProcessMemory"/>, or default and false. Never throws.</summary>
    public static bool TryRead<T>(nint address, out T value) where T : unmanaged => ProcessMemory.Instance.TryRead(address, out value);
}
