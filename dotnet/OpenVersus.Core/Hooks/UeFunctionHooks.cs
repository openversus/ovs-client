using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Resolves the engine functions (FName and FText constructors, FName::ToString) and captures
/// the UFighterGameInstance by hooking its constructor's tail jump. The C++ HookUEFuncs.
/// </summary>
public static unsafe class UeFunctionHooks
{
    private static delegate* unmanaged<nint, nint, nint> s_fighterInstanceCtor;
    private static ILogger? s_log;

    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        c.Log.Info("==UE Funcs==");
        var image = c.Image;

        var ftext = c.Patterns.Find("FText");
        if (!ftext.Found)
        {
            return false;
        }

        nint first = ftext.Address - 26;
        var fromString = GameFunctions.FromCallSite(UE.FTextFromStringName, first + 14, "call at FText-12", "FText* FText::FromString(FText* out, const FString* text)", image);
        GameFunctions.FromCallSite(UE.FTextFromNameName, ftext.Address + 3, "call at FText+3", "FText* FText::FromName(FText* out, FName* name)", image);
        GameFunctions.FromCallSite(UE.FTextGetEmptyName, fromString.Address + 32, "call at FText::FromString+32", "FText* FText::GetEmpty()", image);
        GameFunctions.FromCallSite(UE.FNameToStringName, first, "call at FText-26", "FString* FName::ToString(FName* name, FString* out)", image);

        var cfname = c.Patterns.Find("CFName");
        if (!cfname.Found)
        {
            return false;
        }

        GameFunctions.FromCallSite(UE.FNameCtorCharName, cfname.Address + 79, "call at CFName+79", "void FName::FName(FName* this, const char* name, EFindName find)", image);

        var wcfname = c.Patterns.Find("WCFName");
        if (!wcfname.Found)
        {
            return false;
        }

        GameFunctions.Register(UE.FNameCtorWideName, wcfname.Address - 32, FunctionSource.Pattern, "WCFName-32", "void FName::FName(FName* this, const wchar_t* name, EFindName find)", image);

        GameFunctions.FromRva(UE.ProcessEventName, GameFunctions.ProcessEventRva, "void UObject::ProcessEvent(UObject* this, UFunction* function, void* params)", image);

        foreach (string name in new[] { UE.FTextFromStringName, UE.FTextFromNameName, UE.FTextGetEmptyName, UE.FNameToStringName, UE.FNameCtorCharName, UE.FNameCtorWideName, UE.ProcessEventName })
        {
            c.Log.Debug(GameFunctions.Find(name)!.ToString());
        }

        var fighter = c.Patterns.Find("FighterInstance");
        if (!fighter.Found)
        {
            return false;
        }
        // The pattern is a lea to the function whose tail jump goes to the constructor.
        nint function = CallSite.Destination(fighter.Address, displacementOffset: 3, instructionLength: 7);
        nint site = function + 14;
        nint original = CallSite.Redirect(site, (nint)(delegate* unmanaged<nint, nint, nint>)&CopyFighterInstance);
        s_fighterInstanceCtor = (delegate* unmanaged<nint, nint, nint>)original;
        GameFunctions.Register(GameUi.FighterGameInstanceCtorName, original, FunctionSource.CallSite, $"jmp at 0x{site:X}", "UFighterGameInstance* UFighterGameInstance::UFighterGameInstance(UFighterGameInstance* this, const uint64_t* a2)", image);
        c.Log.Debug($"FighterInstance proxied at 0x{site:X}: {GameFunctions.Find(GameUi.FighterGameInstanceCtorName)}");

        c.Log.Success("All Required UE Funcs Found!");
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint CopyFighterInstance(nint self, nint a2)
    {
        // The redirect replaced a tail jump to the constructor, so the constructor must run
        // whatever happens in the bookkeeping.
        HookGuard.Run("CopyFighterInstance", self, static self =>
        {
            GameUi.RecordFighterGameInstance(self);
            s_log?.Debug($"UFighterGameInstance constructed at 0x{self:X}");
        });
        return s_fighterInstanceCtor(self, a2);
    }
}
