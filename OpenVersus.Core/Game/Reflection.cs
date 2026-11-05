using OpenVersus.Memory;

namespace OpenVersus.Game;

/// <summary>One parameter of a reflected function, from its FProperty.</summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Type">Its property class, such as IntProperty.</param>
/// <param name="Offset">Where it sits in the parameter block.</param>
/// <param name="Size">The size of one element.</param>
/// <param name="ArrayDim">The element count; 1 for anything but a fixed array.</param>
/// <param name="Flags">Its EPropertyFlags.</param>
public sealed record ReflectedParameter(string Name, string Type, int Offset, int Size, int ArrayDim, ulong Flags)
{
    /// <summary>EPropertyFlags: the parameter is const.</summary>
    public const ulong CPF_ConstParm = 0x2;
    /// <summary>EPropertyFlags: the property is a function parameter rather than a local.</summary>
    public const ulong CPF_Parm = 0x80;
    /// <summary>EPropertyFlags: an out parameter.</summary>
    public const ulong CPF_OutParm = 0x100;
    /// <summary>EPropertyFlags: the function's return value.</summary>
    public const ulong CPF_ReturnParm = 0x400;
    /// <summary>EPropertyFlags: passed by reference.</summary>
    public const ulong CPF_ReferenceParm = 0x8000000;

    /// <summary>Whether this is the function's return value.</summary>
    public bool IsReturn => (Flags & CPF_ReturnParm) != 0;
    /// <summary>Whether it is an out parameter.</summary>
    public bool IsOut => (Flags & CPF_OutParm) != 0;

    /// <summary>Direction, type, name, offset in the parameter block and size, for the log.</summary>
    public override string ToString() => $"{(IsReturn ? "return " : IsOut ? "out " : "")}{Type} {Name} @+0x{Offset:X} ({Size}{(ArrayDim > 1 ? $"x{ArrayDim}" : "")})";
}

/// <summary>
/// Reads what the engine knows about a UFunction: its parameter block and each parameter's
/// name, type, offset and size, from the FProperty chain. Offsets are this build's, from the
/// UE4SS MemberVariableLayout dump.
/// </summary>
public static class Reflection
{
    internal const int UStructChildProperties = 0x60;
    internal const int UFunctionFunctionFlags = 0xC0;
    internal const int UFunctionNumParms = 0xC4;
    internal const int UFunctionParmsSize = 0xC6;
    internal const int UFunctionReturnValueOffset = 0xC8;
    internal const int UFunctionFunc = 0xE8;
    internal const int FFieldClassPrivate = 0x08;
    internal const int FFieldNext = 0x20;
    internal const int FFieldNamePrivate = 0x28;
    internal const int FPropertyArrayDim = 0x38;
    internal const int FPropertyElementSize = 0x3C;
    internal const int FPropertyFlags = 0x40;
    internal const int FPropertyOffset = 0x4C;

    /// <summary>
    /// Reads <paramref name="ufunction"/>'s flags, parameter block size and FProperty chain. A field
    /// that cannot be read comes back as zero, and the chain is followed for at most 64 fields.
    /// </summary>
    public static ReflectedSignature Describe(IMemory memory, IGameNames names, nint ufunction, string className, string functionName, nint ownerClass)
    {
        memory.TryRead(ufunction + UFunctionFunctionFlags, out uint functionFlags);
        memory.TryRead(ufunction + UFunctionNumParms, out byte numParms);
        memory.TryRead(ufunction + UFunctionParmsSize, out ushort parmsSize);
        memory.TryRead(ufunction + UFunctionReturnValueOffset, out ushort returnOffset);
        memory.TryRead(ufunction + UFunctionFunc, out nint nativeFunc);

        var parameters = new List<ReflectedParameter>();
        memory.TryRead(ufunction + UStructChildProperties, out nint field);
        for (int guard = 0; field != 0 && guard < 64; guard++)
        {
            memory.TryRead(field + FPropertyFlags, out ulong flags);
            if ((flags & ReflectedParameter.CPF_Parm) != 0)
            {
                memory.TryRead(field + FFieldNamePrivate, out FName name);
                memory.TryRead(field + FFieldClassPrivate, out nint fieldClass);
                memory.TryRead(fieldClass, out FName typeName);
                memory.TryRead(field + FPropertyOffset, out int offset);
                memory.TryRead(field + FPropertyElementSize, out int size);
                memory.TryRead(field + FPropertyArrayDim, out int dim);
                parameters.Add(new ReflectedParameter(names.ToString(name) ?? $"#{name.Index}", names.ToString(typeName) ?? "?", offset, size, dim, flags));
            }
            if (!memory.TryRead(field + FFieldNext, out field))
            {
                break;
            }
        }
        return new ReflectedSignature(className, functionName, ufunction, ownerClass, functionFlags, numParms, parmsSize, returnOffset == 0xFFFF ? -1 : returnOffset, nativeFunc, parameters);
    }

    /// <summary>Finds and registers a reflected function, or returns null when it does not exist.</summary>
    public static GameFunction? Find(ObjectFinder finder, GameImage image, string className, string functionName)
    {
        string key = $"{className}::{functionName}";
        if (GameFunctions.Find(key) is { } known)
        {
            return known;
        }

        nint ufunction = finder.FindFunction(className, functionName, out nint owner);
        if (ufunction == 0)
        {
            return null;
        }

        var signature = Describe(finder.Memory, finder.Names, ufunction, className, functionName, owner);
        return GameFunctions.Register(new GameFunction
        {
            Name = key,
            Address = ufunction,
            Source = FunctionSource.Reflected,
            Image = image,
            Origin = finder.UsesObjectArray ? "object array" : "heap scan",
            Signature = new FunctionSignature { Reflected = signature, Native = signature.NativeFunc != 0 ? new NativeSignature($"exec thunk at 0x{signature.NativeFunc:X}") : null },
        });
    }

    /// <summary>
    /// Calls a reflected function on <paramref name="target"/> through ProcessEvent, with
    /// <paramref name="parameters"/> laid out as the signature says (ParmsSize bytes; a shorter
    /// span is zero-padded). Out and return values come back in the same span. Game thread only.
    /// </summary>
    public static unsafe void Invoke(GameFunction function, nint target, Span<byte> parameters)
    {
        var sig = function.Signature.Reflected ?? throw new InvalidOperationException($"{function.Name} is not a reflected function");
        if (parameters.Length > sig.ParmsSize)
        {
            throw new ArgumentException($"{function.Name} takes {sig.ParmsSize} bytes of parameters, got {parameters.Length}");
        }

        Span<byte> block = stackalloc byte[Math.Max(sig.ParmsSize, 8)];
        block.Clear();
        parameters.CopyTo(block);
        fixed (byte* p = block)
        {
            UE.ProcessEvent(target, function.Address, (nint)p);
        }

        block[..parameters.Length].CopyTo(parameters);
    }
}
