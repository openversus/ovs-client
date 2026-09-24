using OpenVersus.Memory;
using OpenVersus.Native;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Game;

/// <summary>The first 0x30 bytes of a UObject, as this build lays them out (UE4SS MemberVariableLayout).</summary>
public readonly record struct ObjectHeader(nint Address, nint VTable, uint Flags, nint ClassPrivate, FName Name, nint Outer)
{
    public const uint RF_ClassDefaultObject = 0x10;
    public bool IsDefaultObject => (Flags & RF_ClassDefaultObject) != 0;

    public static bool TryRead(IMemory memory, nint address, out ObjectHeader header)
    {
        Span<byte> raw = stackalloc byte[0x30];
        if (!memory.TryRead(address, raw))
        {
            header = default;
            return false;
        }
        header = FromBytes(address, raw);
        return true;
    }

    public static ObjectHeader FromBytes(nint address, ReadOnlySpan<byte> raw) => new(
        address,
        (nint)BitConverter.ToInt64(raw[Mvs.ObjectVTable..]),
        BitConverter.ToUInt32(raw[8..]),
        (nint)BitConverter.ToInt64(raw[Mvs.ObjectClassPrivate..]),
        new FName { Index = BitConverter.ToInt32(raw[Mvs.ObjectNamePrivate..]), Number = BitConverter.ToInt32(raw[(Mvs.ObjectNamePrivate + 4)..]) },
        (nint)BitConverter.ToInt64(raw[Mvs.ObjectOuterPrivate..]));
}

/// <summary>
/// The engine's global object array (GUObjectArray), the authoritative list of live UObjects.
/// Its RVA comes from the UE4SS session of 2026-04-24, rebased through FName::ToString, which
/// the client also finds by pattern; the two agreeing at runtime is the check that it is right.
/// The layout is Unreal 5.1's chunked array, validated before use; if anything disagrees, the
/// finder falls back to scanning the heap as the C++ client did.
/// </summary>
public sealed class ObjectArray
{
    public const uint GUObjectArrayRva = 0x081C5090;
    internal const int ObjObjectsOffset = 0x10;
    internal const int ItemSize = 24;
    internal const int ChunkItems = 65536;

    private readonly IMemory _memory;
    private readonly nint _chunkTable;
    public int Count { get; }
    public int Chunks { get; }

    private ObjectArray(IMemory memory, nint chunkTable, int count, int chunks)
    {
        _memory = memory;
        _chunkTable = chunkTable;
        Count = count;
        Chunks = chunks;
    }

    public static ObjectArray? Open(GameImage image, IMemory memory, IGameNames names, ILogger log)
    {
        nint objObjects = image.Address(GUObjectArrayRva) + ObjObjectsOffset;
        if (!memory.TryRead(objObjects, out nint chunkTable) ||
            !memory.TryRead(objObjects + 0x10, out int maxElements) ||
            !memory.TryRead(objObjects + 0x14, out int numElements) ||
            !memory.TryRead(objObjects + 0x18, out int maxChunks) ||
            !memory.TryRead(objObjects + 0x1C, out int numChunks))
        {
            log.Warn("object array: header unreadable");
            return null;
        }
        int expectedChunks = (numElements + ChunkItems - 1) / ChunkItems;
        if (chunkTable == 0 || numElements < 1000 || numElements > 20_000_000 || numChunks != expectedChunks || maxChunks < numChunks || maxElements < numElements)
        {
            log.Warn($"object array: header does not look like a chunked array (elements {numElements}/{maxElements}, chunks {numChunks}/{maxChunks})");
            return null;
        }
        var array = new ObjectArray(memory, chunkTable, numElements, numChunks);
        // The first live object must look like one: a vtable inside the image, and a name the
        // engine can print once FName::ToString is available.
        foreach (nint obj in array.Objects().Take(16))
        {
            if (!ObjectHeader.TryRead(memory, obj, out var h) || !image.Contains(h.VTable))
            {
                log.Warn($"object array: entry 0x{obj:X} has no vtable in the image; not using it");
                return null;
            }
            if (names.Ready && names.ToString(h.Name) == null)
            {
                log.Warn($"object array: entry 0x{obj:X} has a name that does not resolve; not using it");
                return null;
            }
        }
        log.Info($"object array at 0x{objObjects - ObjObjectsOffset:X}: {numElements} objects in {numChunks} chunks");
        return array;
    }

    public nint this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
            {
                return 0;
            }

            if (!_memory.TryRead(_chunkTable + (nint)(index / ChunkItems) * sizeof(long), out nint chunk) || chunk == 0)
            {
                return 0;
            }

            return _memory.TryRead(chunk + (nint)(index % ChunkItems) * ItemSize, out nint obj) ? obj : 0;
        }
    }

    /// <summary>Every non-null object, reading each chunk in one go.</summary>
    public IEnumerable<nint> Objects()
    {
        var chunkBytes = new byte[ChunkItems * ItemSize];
        for (int c = 0; c < Chunks; c++)
        {
            if (!_memory.TryRead(_chunkTable + (nint)c * sizeof(long), out nint chunk) || chunk == 0)
            {
                continue;
            }

            int items = Math.Min(ChunkItems, Count - c * ChunkItems);
            if (!_memory.TryRead(chunk, chunkBytes.AsSpan(0, items * ItemSize)))
            {
                continue;
            }

            for (int i = 0; i < items; i++)
            {
                nint obj = (nint)BitConverter.ToInt64(chunkBytes, i * ItemSize);
                if (obj != 0)
                {
                    yield return obj;
                }
            }
        }
    }
}

/// <summary>
/// Finds classes, instances and UFunctions by name: through the object array when it
/// validated, otherwise by scanning readable heap regions for objects, as the C++ poller did.
/// Every dereference is a guarded read.
/// </summary>
public sealed class ObjectFinder(GameImage image, IMemory memory, IGameNames names, ILogger log, bool tryObjectArray)
{
    private readonly Dictionary<string, nint> _classes = new(StringComparer.Ordinal);

    /// <summary>The memory and names this finder reads, for code that follows what it found.</summary>
    public IMemory Memory => memory;
    public IGameNames Names => names;
    private readonly object _arrayLock = new();
    private ObjectArray? _array;
    private long _nextArrayAttempt;
    private int _classNameIndex = -1;

    public bool UsesObjectArray => _array != null;

    /// <summary>
    /// The object array, opened on first use rather than at plugin load: the plugin loads before
    /// the engine has created a single object, so validating then would always fail. A failed
    /// attempt is retried a few seconds later; the heap scan covers the meantime.
    /// </summary>
    private ObjectArray? Array
    {
        get
        {
            if (_array != null || !tryObjectArray)
            {
                return _array;
            }

            lock (_arrayLock)
            {
                long now = Environment.TickCount64;
                if (_array == null && now >= _nextArrayAttempt)
                {
                    _nextArrayAttempt = now + 5000;
                    _array = ObjectArray.Open(image, memory, names, log);
                    if (_array == null)
                    {
                        log.Warn("object array not usable yet; scanning the heap");
                    }
                }
                return _array;
            }
        }
    }

    /// <summary>The UClass named <paramref name="className"/> (without its U/A prefix), or 0.</summary>
    public nint FindClass(string className)
    {
        lock (_classes)
        {
            if (_classes.TryGetValue(className, out nint cached) && cached != 0)
            {
                return cached;
            }
        }

        FName name = names.Find(className);
        if (name.Index == 0)
        {
            return 0;
        }

        if (_classNameIndex < 0)
        {
            _classNameIndex = names.Find("Class").Index;
        }

        nint found = 0;
        foreach (var h in Candidates(name.Index))
        {
            // A UClass's own class is "Class"; that separates the class from anything else that shares the name.
            if (ObjectHeader.TryRead(memory, h.ClassPrivate, out var cls) && cls.Name.Index == _classNameIndex)
            {
                found = h.Address;
                break;
            }
        }
        if (found != 0)
        {
            lock (_classes)
            {
                _classes[className] = found;
            }
        }

        return found;
    }

    /// <summary>Any live instance of <paramref name="uclass"/> or a subclass, skipping class default objects.</summary>
    public nint FindInstanceOfClass(nint uclass)
    {
        if (uclass == 0)
        {
            return 0;
        }

        foreach (var h in AllObjects())
        {
            if (h.IsDefaultObject || h.ClassPrivate == 0)
            {
                continue;
            }

            if (h.ClassPrivate == uclass || Inherits(memory, h.ClassPrivate, uclass))
            {
                return h.Address;
            }
        }
        return 0;
    }

    public static bool Inherits(IMemory memory, nint uclass, nint ancestor)
    {
        for (int depth = 0; depth < 20 && uclass != 0; depth++)
        {
            if (uclass == ancestor)
            {
                return true;
            }

            if (!memory.TryRead(uclass + Mvs.StructSuperStruct, out uclass))
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// The UFunction <paramref name="functionName"/> declared on <paramref name="className"/>.
    /// Both names must match: several classes declare functions of the same name, and only a
    /// real UFunction (by vtable) may be handed to ProcessEvent.
    /// </summary>
    public nint FindFunction(string className, string functionName, out nint ownerClass)
    {
        ownerClass = 0;
        FName fn = names.Find(functionName), cls = names.Find(className);
        if (fn.Index == 0 || cls.Index == 0)
        {
            return 0;
        }

        nint vtNative = image.Address(Mvs.UFunctionVTableNativeRva), vtBlueprint = image.Address(Mvs.UFunctionVTableBlueprintRva);
        foreach (var h in Candidates(fn.Index))
        {
            if (h.VTable != vtNative && h.VTable != vtBlueprint)
            {
                continue;
            }

            if (!ObjectHeader.TryRead(memory, h.Outer, out var outer) || outer.Name.Index != cls.Index)
            {
                continue;
            }

            ownerClass = h.Outer;
            return h.Address;
        }
        return 0;
    }

    private IEnumerable<ObjectHeader> Candidates(int nameIndex)
    {
        foreach (var h in AllObjects())
        {
            if (h.Name.Index == nameIndex && h.Name.Number == 0)
            {
                yield return h;
            }
        }
    }

    private IEnumerable<ObjectHeader> AllObjects()
    {
        if (Array is { } array)
        {
            foreach (nint obj in array.Objects())
            {
                if (ObjectHeader.TryRead(memory, obj, out var h) && image.Contains(h.VTable))
                {
                    yield return h;
                }
            }

            yield break;
        }
        foreach (var h in HeapScanner.Objects(image))
        {
            yield return h;
        }
    }
}

/// <summary>
/// The C++ poller's way of finding objects: every committed readable region outside the
/// image, copied in and walked eight bytes at a time for something with a vtable in the image.
/// Slow and approximate, kept as the fallback for when the object array cannot be trusted.
/// </summary>
public static class HeapScanner
{
    public static IEnumerable<ObjectHeader> Objects(GameImage image)
    {
        Kernel32.GetSystemInfo(out var si);
        nint address = si.MinimumApplicationAddress;
        nint max = si.MaximumApplicationAddress;
        var buffer = new byte[1 << 20];
        while (address < max)
        {
            if (Kernel32.VirtualQuery(address, out var mbi) == 0)
            {
                break;
            }

            nint next = (nint)((nuint)mbi.BaseAddress + mbi.RegionSize);
            bool candidate = mbi.State == Kernel32.MEM_COMMIT && (mbi.Protect == Kernel32.PAGE_READWRITE || mbi.Protect == Kernel32.PAGE_READONLY)
                && mbi.RegionSize >= 0x100 && mbi.RegionSize < 0x10000000 && !image.Contains(mbi.BaseAddress);
            if (candidate)
            {
                for (nint at = mbi.BaseAddress; at < next; at += buffer.Length)
                {
                    int length = (int)Math.Min(buffer.Length, next - at);
                    if (!CodeWriter.TryRead(at, buffer.AsSpan(0, length)))
                    {
                        break;
                    }

                    for (int i = 0; i + 0x30 <= length; i += 8)
                    {
                        nint vtable = (nint)BitConverter.ToInt64(buffer, i);
                        if (image.Contains(vtable))
                        {
                            yield return ObjectHeader.FromBytes(at + i, buffer.AsSpan(i, 0x30));
                        }
                    }
                }
            }
            if (next <= address)
            {
                break;
            }

            address = next;
        }
    }
}
