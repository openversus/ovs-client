using OpenVersus.Game;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// Memory made of byte blocks at chosen addresses. A read succeeds only when the whole range
/// lies inside one block, and address 0 never reads, so the finder's "stop on an unreadable
/// pointer" paths are exercised rather than fed zeros.
/// </summary>
public sealed class FakeMemory : IMemory
{
    private readonly List<(nint Start, byte[] Bytes)> _blocks = [];

    public byte[] Alloc(nint address, int size)
    {
        var bytes = new byte[size];
        _blocks.Add((address, bytes));
        return bytes;
    }

    public bool TryRead(nint address, Span<byte> into)
    {
        if (address == 0)
        {
            return false;
        }

        foreach (var (start, bytes) in _blocks)
        {
            if (address >= start && address + into.Length <= start + bytes.Length)
            {
                bytes.AsSpan((int)(address - start), into.Length).CopyTo(into);
                return true;
            }
        }

        return false;
    }

    public void Write(nint address, long value) => BitConverter.TryWriteBytes(Slice(address, 8), value);
    public void Write(nint address, int value) => BitConverter.TryWriteBytes(Slice(address, 4), value);
    public void Write(nint address, uint value) => BitConverter.TryWriteBytes(Slice(address, 4), value);
    public void Write(nint address, ushort value) => BitConverter.TryWriteBytes(Slice(address, 2), value);
    public void Write(nint address, byte value) => Slice(address, 1)[0] = value;
    public void Write(nint address, ulong value) => BitConverter.TryWriteBytes(Slice(address, 8), value);

    private Span<byte> Slice(nint address, int length)
    {
        foreach (var (start, bytes) in _blocks)
        {
            if (address >= start && address + length <= start + bytes.Length)
            {
                return bytes.AsSpan((int)(address - start), length);
            }
        }

        throw new ArgumentException($"0x{address:X} is not in any block");
    }
}

/// <summary>A name table from a list: index is position plus one, and an index off the list resolves to nothing.</summary>
public sealed class FakeNames(params string[] names) : IGameNames
{
    private readonly List<string> _names = [.. names];

    public bool Ready { get; set; } = true;

    public FName Find(string text) => new() { Index = _names.IndexOf(text) + 1, Number = 0 };

    public string? ToString(FName name) => name.Index >= 1 && name.Index <= _names.Count ? _names[name.Index - 1] : null;

    public FName Add(string text)
    {
        _names.Add(text);
        return Find(text);
    }
}

/// <summary>
/// A game's worth of objects laid out with the code's own constants (Mvs, ObjectArray,
/// Reflection): a valid chunked object array whose first entries pass validation, and helpers
/// to add objects, classes and functions. The layout constants come from the same dump the
/// code uses, so these tests prove the comparisons, not the offsets; the game checks those.
///
/// Two things to keep in mind when growing it: the array must stay valid (at least 1000
/// objects, one chunk per 65536), because a finder whose array fails validation falls back to
/// HeapScanner, which P/Invokes kernel32 and throws DllNotFoundException on Linux; and every
/// name here has Number 0, so the finder's "Number == 0" filter is never exercised.
/// </summary>
public sealed class FakeGame
{
    private static readonly nint ImageBase = (nint)0x140000000;
    private const int ImageSize = 0x8000000;
    private static readonly nint Heap = (nint)0x200000000;
    private const int ObjectSize = 0x100;
    private const int MinimumObjects = 1024;

    public FakeMemory Memory { get; } = new();
    public FakeNames Names { get; } = new("Filler", "Class");
    public GameImage Image { get; } = new(ImageBase, ImageSize, []);
    public ListLogger Log { get; } = new();
    public nint GenericVTable => ImageBase + 0x1000;
    public nint NativeFunctionVTable => Image.Address(Mvs.UFunctionVTableNativeRva);
    public nint BlueprintFunctionVTable => Image.Address(Mvs.UFunctionVTableBlueprintRva);

    private readonly nint _chunk;
    private nint _nextObject = Heap;
    private int _count;

    public FakeGame()
    {
        // GUObjectArray: chunk table pointer, then max/num elements and max/num chunks.
        nint objObjects = Image.Address(ObjectArray.GUObjectArrayRva) + ObjectArray.ObjObjectsOffset;
        Memory.Alloc(objObjects - ObjectArray.ObjObjectsOffset, 0x40);
        nint chunkTable = Heap - 0x1000;
        Memory.Alloc(chunkTable, 8);
        _chunk = Heap - 0x800000;
        Memory.Alloc(_chunk, ObjectArray.ChunkItems * ObjectArray.ItemSize);
        Memory.Write(objObjects, (long)chunkTable);
        Memory.Write(chunkTable, (long)_chunk);
        Memory.Write(objObjects + 0x18, ObjectArray.ChunkItems); // max chunks
        Memory.Write(objObjects + 0x1C, 1);                       // num chunks
        Memory.Alloc(Heap, ObjectSize * 4096);
        ClassClass = AddObject("Class", GenericVTable, 0, 0);
        Memory.Write(ClassClass + Mvs.ObjectClassPrivate, (long)ClassClass);
        while (_count < MinimumObjects)
        {
            AddObject("Filler", GenericVTable, ClassClass, 0);
        }

        SetCounts();
    }

    /// <summary>The UClass named "Class", which every class object's ClassPrivate points at.</summary>
    public nint ClassClass { get; }

    public nint AddObject(string name, nint vtable, nint classPrivate, nint outer, uint flags = 0)
    {
        FName fname = Names.Find(name).Index == 0 ? Names.Add(name) : Names.Find(name);
        nint address = _nextObject;
        _nextObject += ObjectSize;
        Memory.Write(address + Mvs.ObjectVTable, (long)vtable);
        Memory.Write(address + 8, flags);
        Memory.Write(address + Mvs.ObjectClassPrivate, (long)classPrivate);
        Memory.Write(address + Mvs.ObjectNamePrivate, fname.Index);
        Memory.Write(address + Mvs.ObjectNamePrivate + 4, fname.Number);
        Memory.Write(address + Mvs.ObjectOuterPrivate, (long)outer);
        Memory.Write(_chunk + (nint)_count * ObjectArray.ItemSize, (long)address);
        _count++;
        SetCounts();
        return address;
    }

    public nint AddClass(string name, nint super = 0)
    {
        nint uclass = AddObject(name, GenericVTable, ClassClass, 0);
        Memory.Write(uclass + Mvs.StructSuperStruct, (long)super);
        return uclass;
    }

    public nint AddInstance(nint uclass, string name = "Instance", bool defaultObject = false) =>
        AddObject(name, GenericVTable, uclass, 0, defaultObject ? ObjectHeader.RF_ClassDefaultObject : 0);

    /// <summary>A UFunction with a parameter block; properties are chained through ChildProperties.</summary>
    public nint AddFunction(string name, nint vtable, nint outer, uint flags = 0, byte numParms = 0, ushort parmsSize = 0, ushort returnOffset = 0xFFFF, nint nativeFunc = 0, nint firstProperty = 0)
    {
        nint fn = AddObject(name, vtable, ClassClass, outer);
        Memory.Write(fn + Reflection.UFunctionFunctionFlags, flags);
        Memory.Write(fn + Reflection.UFunctionNumParms, numParms);
        Memory.Write(fn + Reflection.UFunctionParmsSize, parmsSize);
        Memory.Write(fn + Reflection.UFunctionReturnValueOffset, returnOffset);
        Memory.Write(fn + Reflection.UFunctionFunc, (long)nativeFunc);
        Memory.Write(fn + Reflection.UStructChildProperties, (long)firstProperty);
        return fn;
    }

    /// <summary>An FProperty, not an object: its own block, with a field class whose first qword is a name.</summary>
    public nint AddProperty(string name, string type, ulong flags, int offset, int size, int arrayDim = 1, nint next = 0)
    {
        nint property = _nextObject;
        _nextObject += ObjectSize;
        nint fieldClass = property + 0x80;
        FName typeName = Names.Find(type).Index == 0 ? Names.Add(type) : Names.Find(type);
        FName fname = Names.Find(name).Index == 0 ? Names.Add(name) : Names.Find(name);
        Memory.Write(fieldClass, typeName.Index);
        Memory.Write(fieldClass + 4, typeName.Number);
        Memory.Write(property + Reflection.FFieldClassPrivate, (long)fieldClass);
        Memory.Write(property + Reflection.FFieldNext, (long)next);
        Memory.Write(property + Reflection.FFieldNamePrivate, fname.Index);
        Memory.Write(property + Reflection.FFieldNamePrivate + 4, fname.Number);
        Memory.Write(property + Reflection.FPropertyArrayDim, arrayDim);
        Memory.Write(property + Reflection.FPropertyElementSize, size);
        Memory.Write(property + Reflection.FPropertyFlags, flags);
        Memory.Write(property + Reflection.FPropertyOffset, offset);
        return property;
    }

    public ObjectFinder Finder() => new(Image, Memory, Names, Log, tryObjectArray: true);

    private void SetCounts()
    {
        nint objObjects = Image.Address(ObjectArray.GUObjectArrayRva) + ObjectArray.ObjObjectsOffset;
        Memory.Write(objObjects + 0x10, _count); // max elements
        Memory.Write(objObjects + 0x14, _count); // num elements
    }

    public void CorruptChunkCount(int chunks)
    {
        nint objObjects = Image.Address(ObjectArray.GUObjectArrayRva) + ObjectArray.ObjObjectsOffset;
        Memory.Write(objObjects + 0x1C, chunks);
    }
}
