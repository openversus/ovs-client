namespace OpenVersus.Game;

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
public sealed record NativeSignature(string Declaration);

/// <summary>A UFunction as the engine describes it: where it is, its parameter block, and each parameter.</summary>
public sealed record ReflectedSignature(string ClassName, string FunctionName, nint UFunction, nint OwnerClass,
	uint FunctionFlags, int NumParms, int ParmsSize, int ReturnValueOffset, nint NativeFunc, IReadOnlyList<ReflectedParameter> Parameters)
{
	public override string ToString() =>
		$"{ClassName}::{FunctionName}({string.Join(", ", Parameters)}) parms {ParmsSize} bytes, flags 0x{FunctionFlags:X}";
}

/// <summary>Native or reflected, whichever applies; a function can be reachable both ways.</summary>
public sealed class FunctionSignature
{
	public NativeSignature? Native { get; init; }
	public ReflectedSignature? Reflected { get; init; }

	public override string ToString() =>
		Native?.Declaration ?? (Reflected != null ? $"{Reflected.ClassName}::{Reflected.FunctionName} (reflected)" : "unknown");
}

/// <summary>
/// One function in the game, however it was found: where it is, how, and what it looks like.
/// The address is called through a typed function pointer at the use site
/// (<c>(delegate* unmanaged&lt;...&gt;)fn.Address</c>), since a function pointer type cannot be
/// a generic argument. A reflected function is invoked through <see cref="ReflectedInvoke"/>.
/// </summary>
public sealed class GameFunction
{
	public required string Name { get; init; }
	public required nint Address { get; init; }
	public required FunctionSource Source { get; init; }
	/// <summary>How it was found, for the log: "call at FText+3", "pattern+30", "rva".</summary>
	public required string Origin { get; init; }
	public required FunctionSignature Signature { get; init; }
	public GameImage? Image { get; init; }

	public bool Resolved => Address != 0;
	public uint Rva => Address != 0 && Image != null && Image.Contains(Address) ? Image.Rva(Address) : 0;

	public override string ToString() =>
		Resolved ? $"{Name} at 0x{Address:X} (rva 0x{Rva:X}, {Source}: {Origin}) {Signature}"
		         : $"{Name} unresolved ({Source}: {Origin})";
}
