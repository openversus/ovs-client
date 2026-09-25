using System.Runtime.InteropServices;
using OpenVersus.Native;

namespace OpenVersus.Memory;

/// <summary>
/// A guarded read of another party's memory: the game's heap in the plugin, a byte array in a
/// test. It never throws and never faults; a range that is not readable in full is a false.
/// </summary>
public interface IMemory
{
    /// <summary>Fills <paramref name="into"/> from <paramref name="address"/>; false when any of the range cannot be read.</summary>
    bool TryRead(nint address, Span<byte> into);
}

/// <summary>Typed reads on any <see cref="IMemory"/>.</summary>
public static class MemoryReads
{
    /// <summary>One unmanaged value at <paramref name="address"/>, or default and false.</summary>
    public static bool TryRead<T>(this IMemory memory, nint address, out T value) where T : unmanaged
    {
        value = default;
        return memory.TryRead(address, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
    }
}

/// <summary>
/// This process's memory through ReadProcessMemory, which reports an unmapped page instead of
/// faulting. NativeAOT turns an access violation outside the null page into a fail-fast, so
/// anything read from a pointer that came out of the game's heap goes through here.
/// </summary>
public sealed class ProcessMemory : IMemory
{
    /// <summary>The only instance.</summary>
    public static ProcessMemory Instance { get; } = new();

    private ProcessMemory()
    {
    }

    /// <inheritdoc/>
    public bool TryRead(nint address, Span<byte> into) =>
        Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), address, into, (nuint)into.Length, out nuint read) && read == (nuint)into.Length;
}
