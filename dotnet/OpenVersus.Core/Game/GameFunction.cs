namespace OpenVersus.Game;

/// <summary>How a <see cref="GameFunction"/> was found.</summary>
public enum FunctionSource
{
    /// <summary>A pattern match, plus an offset.</summary>
    Pattern,
    /// <summary>The destination of a call or jmp found near a pattern match.</summary>
    CallSite,
    /// <summary>A fixed RVA in the final build.</summary>
    Rva,
    /// <summary>A UFunction found through Unreal's reflection, callable with ProcessEvent.</summary>
    Reflected,
}

/// <summary>
/// What is known about a native function's shape. The binary carries no metadata for these,
/// so it is whatever the reverse-engineering established, written as C for the reader; the
/// typed function pointer at the call site is the part the compiler checks.
/// </summary>
/// <param name="Declaration">The declaration, written as C.</param>
public sealed record NativeSignature(string Declaration);

/// <summary>A UFunction as the engine describes it: where it is, its parameter block, and each parameter.</summary>
/// <param name="ClassName">The class the function was looked up on.</param>
/// <param name="FunctionName">The function's name.</param>
/// <param name="UFunction">The UFunction object.</param>
/// <param name="OwnerClass">The class that declares it.</param>
/// <param name="FunctionFlags">Its EFunctionFlags.</param>
/// <param name="NumParms">How many properties it has, return value included.</param>
/// <param name="ParmsSize">The size of its parameter block in bytes.</param>
/// <param name="ReturnValueOffset">Where the return value sits in the block; -1 when there is none.</param>
/// <param name="NativeFunc">Its exec thunk; 0 when it has none.</param>
/// <param name="Parameters">Its parameters, in the order the engine lists them.</param>
public sealed record ReflectedSignature(string ClassName, string FunctionName, nint UFunction, nint OwnerClass,
    uint FunctionFlags, int NumParms, int ParmsSize, int ReturnValueOffset, nint NativeFunc, IReadOnlyList<ReflectedParameter> Parameters)
{
    /// <summary>The engine's description on one line: each parameter, the parameter block's size and the function flags.</summary>
    public override string ToString() =>
        $"{ClassName}::{FunctionName}({string.Join(", ", Parameters)}) parms {ParmsSize} bytes, flags 0x{FunctionFlags:X}";
}

/// <summary>Native or reflected, whichever applies; a function can be reachable both ways.</summary>
public sealed class FunctionSignature
{
    /// <summary>The declaration as the reverse-engineering established it; for a reflected function with native code, its exec thunk's address.</summary>
    public NativeSignature? Native { get; init; }
    /// <summary>The engine's description, when the function is a UFunction.</summary>
    public ReflectedSignature? Reflected { get; init; }

    /// <summary>The native declaration if there is one, else the reflected name, else "unknown".</summary>
    public override string ToString() =>
        Native?.Declaration ?? (Reflected != null ? $"{Reflected.ClassName}::{Reflected.FunctionName} (reflected)" : "unknown");
}

/// <summary>
/// One function in the game, however it was found: where it is, how, and what it looks like.
/// The address is called through a typed function pointer at the use site
/// (<c>(delegate* unmanaged&lt;...&gt;)fn.Address</c>), since a function pointer type cannot be
/// a generic argument. A reflected function is invoked through <see cref="Reflection.Invoke"/>.
/// </summary>
public sealed class GameFunction
{
    /// <summary>The name it is registered and logged under: the C++ global's name, or Class::Function for a reflected function.</summary>
    public required string Name { get; init; }
    /// <summary>Where it is; 0 when it was not found. For a reflected function this is the UFunction object, not code.</summary>
    public required nint Address { get; init; }
    /// <summary>How it was found.</summary>
    public required FunctionSource Source { get; init; }
    /// <summary>How it was found, for the log: "call at FText+3", "pattern+30", "rva".</summary>
    public required string Origin { get; init; }
    /// <summary>What is known about its shape.</summary>
    public required FunctionSignature Signature { get; init; }
    /// <summary>The image it lies in, for <see cref="Rva"/>; null when the finder did not say.</summary>
    public GameImage? Image { get; init; }

    /// <summary>True when an address was found.</summary>
    public bool Resolved => Address != 0;
    /// <see cref="Address"/> relative to <see cref="Image"/>; 0 when unresolved, without an image, or outside it.
    public uint Rva => Address != 0 && Image != null && Image.Contains(Address) ? Image.Rva(Address) : 0;

    /// <summary>One log line: name, address, RVA, how it was found and the signature.</summary>
    public override string ToString() =>
        Resolved ? $"{Name} at 0x{Address:X} (rva 0x{Rva:X}, {Source}: {Origin}) {Signature}"
                 : $"{Name} unresolved ({Source}: {Origin})";
}
