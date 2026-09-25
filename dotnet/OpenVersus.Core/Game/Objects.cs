using OpenVersus.Memory;
using OpenVersus.Native;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Game;

/// <summary>The first 0x30 bytes of a UObject, as this build lays them out (UE4SS MemberVariableLayout).</summary>
/// <param name="Address">Where the object is.</param>
/// <param name="VTable">Its vtable pointer, at <see cref="Mvs.ObjectVTable"/>.</param>
/// <param name="Flags">Its EObjectFlags, at +0x08.</param>
/// <param name="ClassPrivate">Its class, at <see cref="Mvs.ObjectClassPrivate"/>.</param>
/// <param name="Name">Its FName, at <see cref="Mvs.ObjectNamePrivate"/>.</param>
/// <param name="Outer">Its outer, at <see cref="Mvs.ObjectOuterPrivate"/>.</param>
public readonly record struct ObjectHeader(nint Address, nint VTable, uint Flags, nint ClassPrivate, FName Name, nint Outer)
{
    /// <summary>EObjectFlags RF_ClassDefaultObject: the object is its class's default object.</summary>
    public const uint RF_ClassDefaultObject = 0x10;
    /// <summary>Whether this is a class default object rather than an instance.</summary>
    public bool IsDefaultObject => (Flags & RF_ClassDefaultObject) != 0;

    /// <summary>Reads the header at <paramref name="address"/>; false, with <paramref name="header"/> zeroed, when the 0x30 bytes are not readable.</summary>
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

    /// <summary>Decodes a header from <paramref name="raw"/>, bytes already read from <paramref name="address"/>. No check that they are an object.</summary>
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
/// Its RVA is agreed by the UE4SS session of 2026-04-24 and by patternsleuth on the final exe.
/// The layout is not Unreal 5.1's: this build keeps the chunked array at +0xA0 of the global
/// and stores the chunk-table pointer XOR-ed with a per-build key, which is why the two tools
/// that assume the stock layout read nothing there. The offsets and the key were read from
/// FUObjectArray::FreeUObjectIndex (rva 0x2D4A570) and confirmed on the live game on
/// 2026-09-24: decoded chunk 0's first object has internal index 0, chunk 1's has 65536. The
/// array is validated before use; if anything disagrees, the finder falls back to scanning the
/// heap as the C++ client did.
/// </summary>
public sealed class ObjectArray
{
    /// <summary>The RVA of GUObjectArray in the final build.</summary>
    public const uint GUObjectArrayRva = 0x081C5090;
    /// <summary>int32 NumElements.</summary>
    internal const int NumElementsOffset = 0xA0;
    /// <summary>int32 MaxElements, then int32 MaxChunks.</summary>
    internal const int MaxElementsOffset = 0xB0;
    internal const int MaxChunksOffset = 0xB4;
    /// <summary>FUObjectItem** Objects, stored XOR <see cref="ObjectsKey"/>.</summary>
    internal const int ObjectsOffset = 0xB8;
    internal const int NumChunksOffset = 0xC0;
    /// <summary>The immediate the engine XORs the chunk-table pointer with, from FreeUObjectIndex.</summary>
    internal const ulong ObjectsKey = 0x01B5DEAFD6B4068C;
    internal const int ItemSize = 24;
    internal const int ChunkItems = 65536;

    private readonly IMemory _memory;
    private readonly nint _global;
    private readonly nint _chunkTable;
    private int _count;
    private int _chunks;
    private bool _indexTrusted = true;

    private ObjectArray(IMemory memory, nint global, nint chunkTable, int count, int chunks)
    {
        _memory = memory;
        _global = global;
        _chunkTable = chunkTable;
        _count = count;
        _chunks = chunks;
    }

    /// <summary>Live object count: the game keeps creating objects after the array is opened, so it is re-read each time.</summary>
    public int Count
    {
        get
        {
            if (_memory.TryRead(_global + NumElementsOffset, out int count) && count >= 0)
            {
                _count = count;
            }

            return _count;
        }
    }

    /// <summary>Chunks in use, re-read each time like <see cref="Count"/>.</summary>
    public int Chunks
    {
        get
        {
            if (_memory.TryRead(_global + NumChunksOffset, out int chunks) && chunks >= 0)
            {
                _chunks = chunks;
            }

            return _chunks;
        }
    }

    /// <summary>
    /// Opens the array at <see cref="GUObjectArrayRva"/> and validates it: the header must look like a
    /// chunked array, and the first objects must have a vtable in the image and, once names can be
    /// asked, a name that resolves. Null, with the reason and the raw header bytes logged, when anything
    /// disagrees.
    /// </summary>
    public static ObjectArray? Open(GameImage image, IMemory memory, IGameNames names, ILogger log)
    {
        nint global = image.Address(GUObjectArrayRva);
        if (!memory.TryRead(global + ObjectsOffset, out ulong encodedTable) ||
            !memory.TryRead(global + MaxElementsOffset, out int maxElements) ||
            !memory.TryRead(global + NumElementsOffset, out int numElements) ||
            !memory.TryRead(global + MaxChunksOffset, out int maxChunks) ||
            !memory.TryRead(global + NumChunksOffset, out int numChunks))
        {
            log.Warn($"object array: header unreadable at 0x{global:X}");
            return null;
        }

        nint chunkTable = (nint)(encodedTable ^ ObjectsKey);
        int expectedChunks = (numElements + ChunkItems - 1) / ChunkItems;
        if (chunkTable == 0 || numElements < 1000 || numElements > 20_000_000 || numChunks != expectedChunks || maxChunks < numChunks || maxElements < numElements)
        {
            log.Warn($"object array: header at 0x{global:X} does not look like a chunked array (elements {numElements}/{maxElements}, chunks {numChunks}/{maxChunks}, table 0x{chunkTable:X})");
            DumpBytes(memory, global, log);
            return null;
        }

        var array = new ObjectArray(memory, global, chunkTable, numElements, numChunks);
        if (!Validate(array, image, memory, names, log, global))
        {
            DumpBytes(memory, global, log);
            return null;
        }

        return array;
    }

    /// <summary>What is actually there, so a failure can be read as "wrong address" or "wrong layout" from the log alone.</summary>
    private static void DumpBytes(IMemory memory, nint global, ILogger log)
    {
        Span<byte> bytes = stackalloc byte[0xD0];
        log.Warn(memory.TryRead(global, bytes)
            ? $"object array: bytes at 0x{global:X} (rva 0x{GUObjectArrayRva:X}): {Convert.ToHexString(bytes)}"
            : $"object array: 0x{global:X} (rva 0x{GUObjectArrayRva:X}) is not readable");
    }

    private static bool Validate(ObjectArray array, GameImage image, IMemory memory, IGameNames names, ILogger log, nint global)
    {
        // The first live objects must look like objects: a vtable inside the image, and a name
        // the engine can print once FName::ToString is available. And there must be some: a
        // table pointer that decodes to garbage yields no readable chunk, hence no objects.
        int examined = 0;
        foreach (nint obj in array.Objects().Take(16))
        {
            examined++;
            if (!ObjectHeader.TryRead(memory, obj, out var h) || !image.Contains(h.VTable))
            {
                log.Warn($"object array: entry 0x{obj:X} has no vtable in the image; not using it");
                return false;
            }
            if (names.Ready && names.ToString(h.Name) == null)
            {
                log.Warn($"object array: entry 0x{obj:X} has a name that does not resolve; not using it");
                return false;
            }
        }
        if (examined == 0)
        {
            log.Warn($"object array: no readable objects behind the table at 0x{array._chunkTable:X}; not using it");
            return false;
        }

        // The internal index is what makes a liveness check O(1); if it does not name the slot the
        // object sits in, the check is switched off rather than the array.
        for (int i = 0; i < Math.Min(array.Count, 64); i++)
        {
            nint obj = array[i];
            if (obj != 0 && memory.TryRead(obj + Mvs.ObjectInternalIndex, out int index) && index != i)
            {
                log.Warn($"object array: entry 0x{obj:X} in slot {i} has internal index {index}; liveness checks through the array are off");
                array._indexTrusted = false;
                break;
            }
        }

        log.Info($"object array at 0x{global:X}: {array.Count} objects in {array.Chunks} chunks");
        return true;
    }

    /// <summary>
    /// Whether the array still lists <paramref name="obj"/> in the slot the object names as its
    /// own: two guarded reads, no walk. An object the engine has freed leaves its slot while its
    /// bytes stay readable, which is what a stale pointer would otherwise keep sampling. Always
    /// true when the internal index could not be validated.
    /// </summary>
    public bool Contains(nint obj) =>
        !_indexTrusted || (_memory.TryRead(obj + Mvs.ObjectInternalIndex, out int index) && index >= 0 && this[index] == obj);

    /// <summary>The object in slot <paramref name="index"/>, or 0 when the slot is empty, out of range or unreadable.</summary>
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
        int count = Count, chunks = Chunks;
        for (int c = 0; c < chunks; c++)
        {
            if (!_memory.TryRead(_chunkTable + (nint)c * sizeof(long), out nint chunk) || chunk == 0)
            {
                continue;
            }

            int items = Math.Min(ChunkItems, count - c * ChunkItems);
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

    /// <summary>The image this finder reads, for code that follows what it found.</summary>
    public GameImage Image => image;
    /// <summary>The memory this finder reads.</summary>
    public IMemory Memory => memory;
    /// <summary>The name table this finder asks.</summary>
    public IGameNames Names => names;
    private readonly object _arrayLock = new();
    private ObjectArray? _array;
    private long _nextArrayAttempt;
    private int _arrayAttempts;
    private HashSet<int>? _classClassNames;

    /// <summary>Attempts to open the object array before giving up on it for the session.</summary>
    public const int MaxArrayAttempts = 12;

    /// <summary>What a class object's own class is called: native, editor-made, and editor-made widget.</summary>
    private static readonly string[] ClassClassNames = ["Class", "BlueprintGeneratedClass", "WidgetBlueprintGeneratedClass"];

    /// <summary>Whether the object array has been opened; false means lookups scan the heap.</summary>
    public bool UsesObjectArray => _array != null;

    /// <summary>Whether <paramref name="obj"/> is still in the object array; true when there is no array to ask (the heap scan cannot tell).</summary>
    public bool IsLive(nint obj) => _array is not { } array || array.Contains(obj);

    /// <summary>
    /// The object array, opened on first use rather than at plugin load: the plugin loads before
    /// the engine has created a single object, so validating then would always fail. A failed
    /// attempt is retried a few seconds later, <see cref="MaxArrayAttempts"/> times; after that
    /// the heap scan is the finder for the rest of the session, said once.
    /// </summary>
    private ObjectArray? Array
    {
        get
        {
            if (_array != null || !tryObjectArray || _arrayAttempts >= MaxArrayAttempts)
            {
                return _array;
            }

            lock (_arrayLock)
            {
                long now = Environment.TickCount64;
                if (_array == null && _arrayAttempts < MaxArrayAttempts && now >= _nextArrayAttempt)
                {
                    _nextArrayAttempt = now + 5000;
                    _arrayAttempts++;
                    _array = ObjectArray.Open(image, memory, names, log);
                    if (_array == null)
                    {
                        log.Warn(_arrayAttempts < MaxArrayAttempts
                            ? $"object array not usable yet; scanning the heap (attempt {_arrayAttempts} of {MaxArrayAttempts})"
                            : $"object array never validated in {MaxArrayAttempts} attempts; scanning the heap for the rest of this session");
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

        // A class object's own class is "Class" for a native class and "BlueprintGeneratedClass"
        // for one made in the editor (the "_C" ones, such as MatchPlayerData_C); that is what
        // separates the class from anything else that shares its name. Resolved once all exist.
        if (_classClassNames == null || _classClassNames.Count < ClassClassNames.Length)
        {
            _classClassNames = [.. ClassClassNames.Select(n => names.Find(n).Index).Where(i => i != 0)];
        }

        nint found = 0;
        foreach (var h in Candidates(name.Index))
        {
            if (ObjectHeader.TryRead(memory, h.ClassPrivate, out var cls) && _classClassNames.Contains(cls.Name.Index))
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
    public nint FindInstanceOfClass(nint uclass) => FindInstancesOfClass(uclass).FirstOrDefault();

    /// <summary>Every live instance of <paramref name="uclass"/> or a subclass, skipping class default objects.</summary>
    public IEnumerable<nint> FindInstancesOfClass(nint uclass)
    {
        if (uclass == 0)
        {
            yield break;
        }

        foreach (var h in AllObjects())
        {
            if (h.IsDefaultObject || h.ClassPrivate == 0)
            {
                continue;
            }

            if (h.ClassPrivate == uclass || Inherits(memory, h.ClassPrivate, uclass))
            {
                yield return h.Address;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="uclass"/> is <paramref name="ancestor"/> or derives from it, following
    /// the super-struct chain for at most 20 levels. False when a link cannot be read.
    /// </summary>
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
    /// <summary>
    /// Every eight-byte-aligned spot in committed read-only or read-write regions outside
    /// <paramref name="image"/> whose first qword points into the image. Regions under 256 bytes or
    /// over 256 MB are skipped. Not every hit is an object; callers filter by name and class.
    /// </summary>
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
