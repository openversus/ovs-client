using System.Runtime.InteropServices;
using OpenVersus.Native;

namespace OpenVersus.Memory;

/// <summary>Writes into the mapped image and reports what is actually there afterwards.</summary>
public static unsafe class CodeWriter
{
    /// <summary>
    /// Null on success, else why not. Code pages stay executable while written: another thread
    /// may be running them, and a page flipped to read-write faults under it. The write is read
    /// back, so a success means the bytes are in memory, not that the call returned.
    /// </summary>
    public static string? Write(nint target, ReadOnlySpan<byte> bytes, bool code)
    {
        uint protect = code ? Kernel32.PAGE_EXECUTE_READWRITE : Kernel32.PAGE_READWRITE;
        if (!Kernel32.VirtualProtect(target, (nuint)bytes.Length, protect, out uint oldProtect))
        {
            return $"VirtualProtect error {Marshal.GetLastPInvokeError()} at 0x{target:X}";
        }

        bytes.CopyTo(new Span<byte>((void*)target, bytes.Length));
        if (code)
        {
            Kernel32.FlushInstructionCache(Kernel32.GetCurrentProcess(), target, (nuint)bytes.Length);
        }

        if (!Kernel32.VirtualProtect(target, (nuint)bytes.Length, oldProtect, out _))
        {
            return $"written, but protection not restored (VirtualProtect error {Marshal.GetLastPInvokeError()}) at 0x{target:X}";
        }

        if (!new ReadOnlySpan<byte>((void*)target, bytes.Length).SequenceEqual(bytes))
        {
            return $"read back {Hex(Read(target, bytes.Length))} at 0x{target:X}, wrote {Hex(bytes)}";
        }

        return null;
    }

    /// <summary>Writes only if the bytes there are <paramref name="expected"/>. Null on success.</summary>
    public static string? WriteIf(nint target, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> bytes, bool code)
    {
        var current = new ReadOnlySpan<byte>((void*)target, expected.Length);
        if (!current.SequenceEqual(expected))
        {
            return $"expected {Hex(expected)} at 0x{target:X}, found {Hex(current)}; not patching";
        }

        return Write(target, bytes, code);
    }

    public static byte[] Read(nint address, int length) => new ReadOnlySpan<byte>((void*)address, length).ToArray();

    /// <summary>
    /// A read that cannot take the process down. NativeAOT turns an access violation outside the
    /// null page into a fail-fast, so anything read from a pointer that came out of the game's
    /// heap goes through here rather than a dereference.
    /// </summary>
    public static bool TryRead(nint address, Span<byte> into)
    {
        fixed (byte* p = into)
        {
            return Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), address, p, (nuint)into.Length, out nuint read)
                && read == (nuint)into.Length;
        }
    }

    public static bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        fixed (T* p = &value)
        {
            return Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), address, p, (nuint)sizeof(T), out nuint read)
                && read == (nuint)sizeof(T);
        }
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
