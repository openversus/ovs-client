using System.Runtime.InteropServices;
using OpenVersus.Native;

namespace OpenVersus.Memory;

/// <summary>
/// Pages of executable memory within a rel32 jump of a code address, holding twelve-byte
/// stubs ("mov rax, imm64; jmp rax") so a five-byte call or jmp in the game can reach a
/// function anywhere in the address space. Pages are never freed; the game unmaps them on exit.
/// A port of the C++ client's Trampoline class, which searched only above the address; this one
/// also searches below, since the game image can sit near the top of the usable range.
///
/// By default the page is read-execute at rest and made writable only while a stub is placed
/// (execute stays on during the write, so a game thread running an earlier stub never faults).
/// Built with OVS_RWX_TRAMPOLINES (`-p:RwxTrampolines=true`) the page stays read-write-execute
/// for its whole life, as the C++ client's did.
/// </summary>
public sealed unsafe class Trampoline
{
    private const int StubSize = 12;
    private static readonly List<Trampoline> s_pages = [];
    private static readonly object s_lock = new();

    private readonly nuint _size;
    private byte* _memory;
    private nuint _left;

    private Trampoline(byte* memory, nuint size)
    {
        _memory = memory;
        _size = size;
        _left = size;
    }

    /// <summary>The start of the page; <see cref="Near"/> hands it out only for code within rel32 reach of it.</summary>
    public nint Base { get; private init; }

    /// <summary>A trampoline page usable from code at <paramref name="address"/>, allocating one if none fits.</summary>
    public static Trampoline Near(nint address)
    {
        lock (s_lock)
        {
            foreach (Trampoline page in s_pages)
            {
                if (page.Feasible(address))
                {
                    return page;
                }
            }

            Kernel32.GetSystemInfo(out SYSTEM_INFO info);
            uint size = info.AllocationGranularity;
            nint memory = FindAndAllocate(address, size);
            if (memory == 0)
            {
                throw new PatchException($"no free page within 2 GB of 0x{address:X} for trampolines (error {Marshal.GetLastPInvokeError()})");
            }

            var fresh = new Trampoline((byte*)memory, size) { Base = memory };
            s_pages.Add(fresh);
            return fresh;
        }
    }

    private bool Feasible(nint address) => Reachable((nint)_memory, address) && _left >= StubSize;

    /// <summary>True if <paramref name="from"/> can reach <paramref name="to"/> with a rel32 displacement.</summary>
    public static bool Reachable(nint from, nint to)
    {
        long diff = (long)to - (long)from;
        return diff >= int.MinValue && diff <= int.MaxValue;
    }

    /// <summary>The protection the page has when nothing is being written to it.</summary>
    public static uint RestingProtection =>
#if OVS_RWX_TRAMPOLINES
        Kernel32.PAGE_EXECUTE_READWRITE;
#else
        Kernel32.PAGE_EXECUTE_READ;
#endif

    /// <summary>A stub that jumps to <paramref name="target"/>; returns the stub's address.</summary>
    public nint Jump(nint target)
    {
        lock (s_lock)
        {
            byte* stub = Reserve(StubSize, 1);
            Unseal();
            // mov rax, imm64
            stub[0] = 0x48;
            stub[1] = 0xB8;
            *(long*)(stub + 2) = target;
            // jmp rax
            stub[10] = 0xFF;
            stub[11] = 0xE0;
            Kernel32.FlushInstructionCache(Kernel32.GetCurrentProcess(), (nint)stub, StubSize);
            Seal();
            return (nint)stub;
        }
    }

    /// <summary>Writes <paramref name="bytes"/> into reserved page space, for data the game must reach with rel32 addressing.</summary>
    public nint Place(ReadOnlySpan<byte> bytes, int align)
    {
        lock (s_lock)
        {
            byte* space = Reserve(bytes.Length, align);
            Unseal();
            bytes.CopyTo(new Span<byte>(space, bytes.Length));
            Seal();
            return (nint)space;
        }
    }

    private void Unseal()
    {
#if !OVS_RWX_TRAMPOLINES
        if (!Kernel32.VirtualProtect(Base, _size, Kernel32.PAGE_EXECUTE_READWRITE, out _))
        {
            throw new PatchException($"could not make trampoline page 0x{Base:X} writable (error {Marshal.GetLastPInvokeError()})");
        }
#endif
    }

    private void Seal()
    {
#if !OVS_RWX_TRAMPOLINES
        if (!Kernel32.VirtualProtect(Base, _size, Kernel32.PAGE_EXECUTE_READ, out _))
        {
            throw new PatchException($"could not seal trampoline page 0x{Base:X} (error {Marshal.GetLastPInvokeError()})");
        }
#endif
    }

    private byte* Reserve(int size, int align)
    {
        nuint misalign = (nuint)_memory % (nuint)align;
        nuint pad = misalign == 0 ? 0 : (nuint)align - misalign;
        if (_left < pad + (nuint)size)
        {
            throw new PatchException("out of trampoline space");
        }

        byte* space = _memory + pad;
        _memory += pad + (nuint)size;
        _left -= pad + (nuint)size;
        return space;
    }

    private static nint FindAndAllocate(nint near, uint size)
    {
        // Above first, as the original did, then below.
        nint page = Search(near, size, up: true);
        return page != 0 ? page : Search(near, size, up: false);
    }

    private static nint Search(nint near, uint size, bool up)
    {
        nint current = near;
        while (true)
        {
            if (Kernel32.VirtualQuery(current, out MEMORY_BASIC_INFORMATION mbi) == 0)
            {
                return 0;
            }

            if (mbi.State == Kernel32.MEM_FREE && mbi.RegionSize >= size)
            {
                // Try the lowest and the highest granularity-aligned page of the free region that
                // stays reachable; either may be the one within range.
                nint regionStart = mbi.BaseAddress;
                nint regionEnd = (nint)((nuint)mbi.BaseAddress + mbi.RegionSize);
                nint low = (nint)(((nuint)regionStart + size - 1) & ~(nuint)(size - 1));
                nint high = (nint)(((nuint)regionEnd - size) & ~(nuint)(size - 1));
                foreach (nint candidate in up ? new[] { low, high } : new[] { high, low })
                {
                    if (candidate < regionStart || candidate + (nint)size > regionEnd || !Reachable(candidate, near))
                    {
                        continue;
                    }

                    nint mem = Kernel32.VirtualAlloc(candidate, size, Kernel32.MEM_COMMIT | Kernel32.MEM_RESERVE, RestingProtection);
                    if (mem != 0)
                    {
                        return mem;
                    }
                }
            }
            if (up)
            {
                nint next = (nint)((nuint)mbi.BaseAddress + mbi.RegionSize);
                if (next <= current || !Reachable(next, near))
                {
                    return 0;
                }

                current = next;
            }
            else
            {
                if (mbi.BaseAddress == 0)
                {
                    return 0;
                }

                nint next = mbi.BaseAddress - 1;
                if (!Reachable(next, near))
                {
                    return 0;
                }

                current = next;
            }
        }
    }
}
