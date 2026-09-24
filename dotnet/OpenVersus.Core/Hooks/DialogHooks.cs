using OpenVersus.Game;
using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>Resolves what the dialog helpers need. Nothing is patched; the C++ DialogHooks.</summary>
public static class DialogHooks
{
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==Dialog Funcs==");
        if (!c.Status.UeFuncs)
        {
            c.Log.Error("UE Funcs were not enabled therefore Dialog cannot be used!");
            return false;
        }
        var dialog = c.Patterns.Find("Dialog", c.Settings.Pattern("pDialog"));
        if (!dialog.Found)
        {
            return false;
        }

        GameFunctions.Register(GameUi.AddDialogName, dialog.Address + 30, FunctionSource.Pattern, "Dialog+30", "UMvsDialog* UMvsFrontendManager::AddDialog(UMvsFrontendManager* this, FMvsDialogParameters* params)", c.Image);
        GameFunctions.FromCallSite(GameUi.GetFrontendManagerName, dialog.Address + 38, "call at Dialog+38", "UMvsFrontendManager* GetFrontendManager(UFighterGameInstance* instance)", c.Image);

        var parameters = c.Patterns.Find("DialogParams", c.Settings.Pattern("pDialogParams"));
        if (parameters.Found)
        {
            GameFunctions.Register(GameUi.DialogParametersCtorName, parameters.Address, FunctionSource.Pattern, "DialogParams", "void FMvsDialogParameters::FMvsDialogParameters(FMvsDialogParameters* this, FText* prompt, uint8_t flags)", c.Image);
        }

        var callback = c.Patterns.Find("DialogCallback", c.Settings.Pattern("pDialogCallback"));
        if (!callback.Found)
        {
            return false;
        }

        GameFunctions.FromCallSite(GameUi.DialogCallbackSetterName, callback.Address, "call at DialogCallback", "void SingleParamDialogCallbackSetter(TMulticastDelegateBase* button, uint64_t* result, UMvsDialog* dialog, UMvsDialog** self)", c.Image);

        var quit = c.Patterns.Find("QuitGameCallback", c.Settings.Pattern("pQuitGameCallback"));
        if (quit.Found)
        {
            GameFunctions.Register(GameUi.QuitGameName, quit.Address, FunctionSource.Pattern, "QuitGameCallback", "bool QuitGame(UMvsDialog* dialog)", c.Image);
        }
        else
        {
            c.Log.Warn("Couldn't find pQuitGameCallback Pattern. The refund dialog's Disagree button will do nothing.");
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
