using OpenVersus.Memory;

namespace OpenVersus.Game;

/// <summary>One parameter of a reflected function, from its FProperty.</summary>
public sealed record ReflectedParameter(string Name, string Type, int Offset, int Size, int ArrayDim, ulong Flags)
{
    public const ulong CPF_ConstParm = 0x2;
    public const ulong CPF_Parm = 0x80;
    public const ulong CPF_OutParm = 0x100;
    public const ulong CPF_ReturnParm = 0x400;
    public const ulong CPF_ReferenceParm = 0x8000000;

    public bool IsReturn => (Flags & CPF_ReturnParm) != 0;
    public bool IsOut => (Flags & CPF_OutParm) != 0;

    public override string ToString() => $"{(IsReturn ? "return " : IsOut ? "out " : "")}{Type} {Name} @+0x{Offset:X} ({Size}{(ArrayDim > 1 ? $"x{ArrayDim}" : "")})";
}

/// <summary>
/// Reads what the engine knows about a UFunction: its parameter block and each parameter's
/// name, type, offset and size, from the FProperty chain. Offsets are this build's, from the
/// UE4SS MemberVariableLayout dump.
/// </summary>
public static class Reflection
{
    private const int UStructChildProperties = 0x60;
    private const int UFunctionFunctionFlags = 0xC0;
    private const int UFunctionNumParms = 0xC4;
    private const int UFunctionParmsSize = 0xC6;
    private const int UFunctionReturnValueOffset = 0xC8;
    private const int UFunctionFunc = 0xE8;
    private const int FFieldClassPrivate = 0x08;
    private const int FFieldNext = 0x20;
    private const int FFieldNamePrivate = 0x28;
    private const int FPropertyArrayDim = 0x38;
    private const int FPropertyElementSize = 0x3C;
    private const int FPropertyFlags = 0x40;
    private const int FPropertyOffset = 0x4C;

    public static ReflectedSignature Describe(nint ufunction, string className, string functionName, nint ownerClass)
    {
        CodeWriter.TryRead(ufunction + UFunctionFunctionFlags, out uint functionFlags);
        CodeWriter.TryRead(ufunction + UFunctionNumParms, out byte numParms);
        CodeWriter.TryRead(ufunction + UFunctionParmsSize, out ushort parmsSize);
        CodeWriter.TryRead(ufunction + UFunctionReturnValueOffset, out ushort returnOffset);
        CodeWriter.TryRead(ufunction + UFunctionFunc, out nint nativeFunc);

        var parameters = new List<ReflectedParameter>();
        CodeWriter.TryRead(ufunction + UStructChildProperties, out nint field);
        for (int guard = 0; field != 0 && guard < 64; guard++)
        {
            CodeWriter.TryRead(field + FPropertyFlags, out ulong flags);
            if ((flags & ReflectedParameter.CPF_Parm) != 0)
            {
                CodeWriter.TryRead(field + FFieldNamePrivate, out FName name);
                CodeWriter.TryRead(field + FFieldClassPrivate, out nint fieldClass);
                CodeWriter.TryRead(fieldClass, out FName typeName);
                CodeWriter.TryRead(field + FPropertyOffset, out int offset);
                CodeWriter.TryRead(field + FPropertyElementSize, out int size);
                CodeWriter.TryRead(field + FPropertyArrayDim, out int dim);
                parameters.Add(new ReflectedParameter(UE.NameToString(name) ?? $"#{name.Index}", UE.NameToString(typeName) ?? "?", offset, size, dim, flags));
            }
            if (!CodeWriter.TryRead(field + FFieldNext, out field))
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

        var signature = Describe(ufunction, className, functionName, owner);
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
