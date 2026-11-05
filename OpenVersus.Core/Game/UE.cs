using System.Runtime.InteropServices;

namespace OpenVersus.Game;

/// <summary>An FName as the engine passes it by value: an entry in the name table and an instance number.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FName
{
    /// <summary>The name's entry in the name table. 0 is None, which is also what a failed lookup returns.</summary>
    public int Index;
    /// <summary>The instance suffix, stored as the displayed number plus one; 0 means no suffix.</summary>
    public int Number;
}

/// <summary>TArray&lt;wchar_t&gt;: a pointer, a count including the terminator, and a capacity.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FString
{
    /// <summary>The characters, terminator included; null when the string was never allocated.</summary>
    public char* Data;
    /// <summary>Characters in use, terminator included; 0 when empty.</summary>
    public int Count;
    /// <summary>Characters allocated.</summary>
    public int Max;

    /// <summary>The text without its terminator; empty when there is none.</summary>
    public override string ToString() => Data == null || Count <= 0 ? "" : new string(Data, 0, Count - 1);
}

/// <summary>A shared-pointer to text data plus flags; 24 bytes. Built only through the game's own constructors.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FText
{
    /// <summary>The text data the shared reference points at.</summary>
    public nint TextData;
    /// <summary>The rest of the 24 bytes: the shared reference's controller and the flags. Never touched here.</summary>
    public fixed byte Pad[0x10];
}

/// <summary>TArray&lt;T&gt; header: data pointer, count, capacity.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TArrayHeader
{
    /// <summary>The elements.</summary>
    public nint Data;
    /// <summary>Elements in use.</summary>
    public int Count;
    /// <summary>Elements allocated.</summary>
    public int Max;
}

/// <summary>One bound delegate, as an entry of a multicast delegate's invocation list.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FDelegateBase
{
    /// <summary>Where the delegate object lives; its first qword is the object's vtable (see <see cref="GameUi.AssignCallbackToButton"/>).</summary>
    public nint DelegateAllocator;
    /// <summary>The size of the delegate object.</summary>
    public int DelegateSize;
    /// <summary>Padding to the next entry.</summary>
    public int Pad;
}

/// <summary>TArray&lt;FDelegateBase&gt; plus two ints; 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TMulticastDelegateBase
{
    /// <summary>The bound delegates, each an <see cref="FDelegateBase"/>.</summary>
    public TArrayHeader InvocationList;
    /// <summary>Engine bookkeeping for compacting the list; not used here.</summary>
    public int CompactionThreshold;
    /// <summary>The engine's lock count while it broadcasts; not used here.</summary>
    public int InvocationListLockCount;
}

/// <summary>What the FName constructor does with a name the table does not have yet.</summary>
public enum EFindName
{
    /// <summary>Look it up only; a missing name comes back as Index 0.</summary>
    Find,
    /// <summary>Add it when it is missing.</summary>
    Add,
    /// <summary>Add it when it is missing, and replace the stored casing when it is not.</summary>
    Replace,
}

/// <summary>
/// The engine functions the client calls, through the addresses HookUEFuncs resolves. Each
/// wrapper reads the registry at call time, so a hook that runs before resolution fails
/// visibly (a zero address) rather than quietly, and each refuses until <see cref="Engine.IsUp"/>.
/// </summary>
public static unsafe class UE
{
    /// <summary>The <see cref="GameFunctions"/> key for FName::ToString.</summary>
    public const string FNameToStringName = "FName::ToString";
    /// <summary>The <see cref="GameFunctions"/> key for the narrow FName constructor, which <see cref="FindName"/> calls.</summary>
    public const string FNameCtorCharName = "FName::FName(char*)";
    /// <summary>The <see cref="GameFunctions"/> key for the wide FName constructor, which <see cref="MakeName"/> calls.</summary>
    public const string FNameCtorWideName = "FName::FName(wchar_t*)";
    /// <summary>The <see cref="GameFunctions"/> key for FText::FromString. Not called; its address is how FText::GetEmpty is found.</summary>
    public const string FTextFromStringName = "FText::FromString";
    /// <summary>The <see cref="GameFunctions"/> key for FText::FromName, which <see cref="MakeText"/> calls.</summary>
    public const string FTextFromNameName = "FText::FromName";
    /// <summary>The <see cref="GameFunctions"/> key for FText::GetEmpty, which <see cref="EmptyText"/> calls.</summary>
    public const string FTextGetEmptyName = "FText::GetEmpty";
    /// <summary>The <see cref="GameFunctions"/> key for UObject::ProcessEvent.</summary>
    public const string ProcessEventName = "UObject::ProcessEvent";

    /// <summary>
    /// True once the wide FName constructor, FText::FromName and FText::GetEmpty have addresses.
    /// It says nothing about whether the engine may be called yet; that is <see cref="Engine.IsUp"/>.
    /// </summary>
    public static bool Ready =>
        GameFunctions.Address(FNameCtorWideName) != 0 && GameFunctions.Address(FTextFromNameName) != 0 && GameFunctions.Address(FTextGetEmptyName) != 0;

    /// <summary>FName(text, FNAME_Add): the name is created if it does not exist.</summary>
    public static FName MakeName(string text)
    {
        Engine.Require("MakeName");
        var fn = (delegate* unmanaged<FName*, char*, int, void>)GameFunctions.Address(FNameCtorWideName);
        if (fn == null)
        {
            throw new InvalidOperationException("FName constructor not resolved");
        }

        FName name;
        fixed (char* p = text)
        {
            fn(&name, p, (int)EFindName.Add);
        }

        return name;
    }

    /// <summary>FName(text, FNAME_Find): Index 0 when the name does not exist.</summary>
    public static FName FindName(string text)
    {
        Engine.Require("FindName");
        var fn = (delegate* unmanaged<FName*, byte*, int, void>)GameFunctions.Address(FNameCtorCharName);
        if (fn == null)
        {
            throw new InvalidOperationException("FName constructor not resolved");
        }

        FName name;
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        fixed (byte* p = ascii)
        {
            fn(&name, p, (int)EFindName.Find);
        }

        return name;
    }

    /// <summary>The string behind an FName, or null if the engine gives nothing sensible back.</summary>
    public static string? NameToString(FName name)
    {
        Engine.Require("NameToString");
        var fn = (delegate* unmanaged<FName*, FString*, FString*>)GameFunctions.Address(FNameToStringName);
        if (fn == null)
        {
            return null;
        }

        FString result = default;
        fn(&name, &result);
        if (result.Data == null || result.Count <= 0 || result.Count >= 1024)
        {
            return null;
        }

        return result.ToString();
    }

    /// <summary>FText(): a copy of the engine's empty text.</summary>
    public static FText EmptyText()
    {
        Engine.Require("EmptyText");
        var fn = (delegate* unmanaged<FText*>)GameFunctions.Address(FTextGetEmptyName);
        if (fn == null)
        {
            throw new InvalidOperationException("FText::GetEmpty not resolved");
        }

        return *fn();
    }

    /// <summary>FText(text), as the C++ client built it: through an FName, then FText::FromName.</summary>
    public static FText MakeText(string text)
    {
        Engine.Require("MakeText");
        var fromName = (delegate* unmanaged<FText*, FName*, FText*>)GameFunctions.Address(FTextFromNameName);
        if (fromName == null)
        {
            throw new InvalidOperationException("FText::FromName not resolved");
        }

        FName name = MakeName(text);
        FText result;
        fromName(&result, &name);
        return result;
    }

    /// <summary>UObject::ProcessEvent(object, function, parameters). Game thread only.</summary>
    public static void ProcessEvent(nint target, nint ufunction, nint parameters)
    {
        Engine.Require("ProcessEvent");
        var fn = (delegate* unmanaged<nint, nint, nint, void>)GameFunctions.Address(ProcessEventName);
        if (fn == null)
        {
            throw new InvalidOperationException("ProcessEvent not resolved");
        }

        fn(target, ufunction, parameters);
    }
}
