using System.Runtime.InteropServices;

namespace OpenVersus.Game;

[StructLayout(LayoutKind.Sequential)]
public struct FName
{
	public int Index;
	public int Number;
}

/// <summary>TArray&lt;wchar_t&gt;: a pointer, a count including the terminator, and a capacity.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FString
{
	public char* Data;
	public int Count;
	public int Max;

	public override string ToString() => Data == null || Count <= 0 ? "" : new string(Data, 0, Count - 1);
}

/// <summary>A shared-pointer to text data plus flags; 24 bytes. Built only through the game's own constructors.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FText
{
	public nint TextData;
	public fixed byte Pad[0x10];
}

/// <summary>TArray&lt;T&gt; header: data pointer, count, capacity.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TArrayHeader
{
	public nint Data;
	public int Count;
	public int Max;
}

[StructLayout(LayoutKind.Sequential)]
public struct FDelegateBase
{
	public nint DelegateAllocator;
	public int DelegateSize;
	public int Pad;
}

/// <summary>TArray&lt;FDelegateBase&gt; plus two ints; 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TMulticastDelegateBase
{
	public TArrayHeader InvocationList;
	public int CompactionThreshold;
	public int InvocationListLockCount;
}

public enum EFindName { Find, Add, Replace }

/// <summary>
/// The engine functions the client calls, through the addresses HookUEFuncs resolves. Each
/// wrapper reads the registry at call time, so a hook that runs before resolution fails
/// visibly (a zero address) rather than quietly.
/// </summary>
public static unsafe class UE
{
	public const string FNameToStringName = "FName::ToString";
	public const string FNameCtorCharName = "FName::FName(char*)";
	public const string FNameCtorWideName = "FName::FName(wchar_t*)";
	public const string FTextFromStringName = "FText::FromString";
	public const string FTextFromNameName = "FText::FromName";
	public const string FTextGetEmptyName = "FText::GetEmpty";
	public const string ProcessEventName = "UObject::ProcessEvent";

	public static bool Ready =>
		GameFunctions.Address(FNameCtorWideName) != 0 && GameFunctions.Address(FTextFromNameName) != 0 && GameFunctions.Address(FTextGetEmptyName) != 0;

	/// <summary>FName(text, FNAME_Add): the name is created if it does not exist.</summary>
	public static FName MakeName(string text)
	{
		var fn = (delegate* unmanaged<FName*, char*, int, void>)GameFunctions.Address(FNameCtorWideName);
		if (fn == null) throw new InvalidOperationException("FName constructor not resolved");
		FName name;
		fixed (char* p = text)
			fn(&name, p, (int)EFindName.Add);
		return name;
	}

	/// <summary>FName(text, FNAME_Find): Index 0 when the name does not exist.</summary>
	public static FName FindName(string text)
	{
		var fn = (delegate* unmanaged<FName*, byte*, int, void>)GameFunctions.Address(FNameCtorCharName);
		if (fn == null) throw new InvalidOperationException("FName constructor not resolved");
		FName name;
		byte[] ascii = System.Text.Encoding.ASCII.GetBytes(text + "\0");
		fixed (byte* p = ascii)
			fn(&name, p, (int)EFindName.Find);
		return name;
	}

	/// <summary>The string behind an FName, or null if the engine gives nothing sensible back.</summary>
	public static string? NameToString(FName name)
	{
		var fn = (delegate* unmanaged<FName*, FString*, FString*>)GameFunctions.Address(FNameToStringName);
		if (fn == null) return null;
		FString result = default;
		fn(&name, &result);
		if (result.Data == null || result.Count <= 0 || result.Count >= 1024)
			return null;
		return result.ToString();
	}

	/// <summary>FText(): a copy of the engine's empty text.</summary>
	public static FText EmptyText()
	{
		var fn = (delegate* unmanaged<FText*>)GameFunctions.Address(FTextGetEmptyName);
		if (fn == null) throw new InvalidOperationException("FText::GetEmpty not resolved");
		return *fn();
	}

	/// <summary>FText(text), as the C++ client built it: through an FName, then FText::FromName.</summary>
	public static FText MakeText(string text)
	{
		var fromName = (delegate* unmanaged<FText*, FName*, FText*>)GameFunctions.Address(FTextFromNameName);
		if (fromName == null) throw new InvalidOperationException("FText::FromName not resolved");
		FName name = MakeName(text);
		FText result;
		fromName(&result, &name);
		return result;
	}

	/// <summary>UObject::ProcessEvent(object, function, parameters). Game thread only.</summary>
	public static void ProcessEvent(nint target, nint ufunction, nint parameters)
	{
		var fn = (delegate* unmanaged<nint, nint, nint, void>)GameFunctions.Address(ProcessEventName);
		if (fn == null) throw new InvalidOperationException("ProcessEvent not resolved");
		fn(target, ufunction, parameters);
	}
}
