using OpenVersus.Game;
using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>Resolves what the dialog helpers need. Nothing is patched; the C++ DialogHooks.</summary>
public static class DialogHooks
{
    /// <summary>
    /// Registers the dialog functions. False, with the reason logged, when the UE functions are not
    /// resolved or the Dialog or DialogCallback pattern is missing; a missing DialogParams or
    /// QuitGameCallback pattern loses only that function.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==Dialog Funcs==");
        if (!c.Status.UeFuncs)
        {
            c.Log.Error("UE Funcs were not enabled therefore Dialog cannot be used!");
            return false;
        }
        var dialog = c.Patterns.Find("Dialog");
        if (!dialog.Found)
        {
            return false;
        }

        GameFunctions.Register(GameUi.AddDialogName, dialog.Address + 30, FunctionSource.Pattern, "Dialog+30", "UMvsDialog* UMvsFrontendManager::AddDialog(UMvsFrontendManager* this, FMvsDialogParameters* params)", c.Image);
        GameFunctions.FromCallSite(GameUi.GetFrontendManagerName, dialog.Address + 38, "call at Dialog+38", "UMvsFrontendManager* GetFrontendManager(UFighterGameInstance* instance)", c.Image);

        var parameters = c.Patterns.Find("DialogParams");
        if (parameters.Found)
        {
            GameFunctions.Register(GameUi.DialogParametersCtorName, parameters.Address, FunctionSource.Pattern, "DialogParams", "void FMvsDialogParameters::FMvsDialogParameters(FMvsDialogParameters* this, FText* prompt, uint8_t flags)", c.Image);
        }

        var callback = c.Patterns.Find("DialogCallback");
        if (!callback.Found)
        {
            return false;
        }

        GameFunctions.FromCallSite(GameUi.DialogCallbackSetterName, callback.Address, "call at DialogCallback", "void SingleParamDialogCallbackSetter(TMulticastDelegateBase* button, uint64_t* result, UMvsDialog* dialog, UMvsDialog** self)", c.Image);

        var quit = c.Patterns.Find("QuitGameCallback");
        if (quit.Found)
        {
            GameFunctions.Register(GameUi.QuitGameName, quit.Address, FunctionSource.Pattern, "QuitGameCallback", "bool QuitGame(UMvsDialog* dialog)", c.Image);
        }
        else
        {
            c.Log.Debug("Couldn't find the QuitGameCallback pattern; nothing calls it since the free-mod notice has only an OK button.");
        }

        foreach (string name in new[] { GameUi.AddDialogName, GameUi.GetFrontendManagerName, GameUi.DialogParametersCtorName, GameUi.DialogCallbackSetterName, GameUi.QuitGameName })
        {
            if (GameFunctions.Find(name) is { } f)
            {
                c.Log.Debug(f.ToString());
            }
        }

        c.Log.Success("Dialog Usable!");
        return true;
    }
}
