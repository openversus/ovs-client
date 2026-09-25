using System.Runtime.InteropServices;

namespace OpenVersus.Game;

/// <summary>
/// The game's dialogs and notifications, through the functions DialogHooks and
/// NotificationHooks resolve. Everything here must run on the game thread.
/// </summary>
public static unsafe class GameUi
{
    /// <summary>The <see cref="GameFunctions"/> key for the function that returns the game instance's UMvsFrontendManager.</summary>
    public const string GetFrontendManagerName = "GetFrontendManager";
    /// <summary>The <see cref="GameFunctions"/> key for UMvsFrontendManager::AddDialog, which opens a dialog.</summary>
    public const string AddDialogName = "UMvsFrontendManager::AddDialog";
    /// <summary>
    /// The <see cref="GameFunctions"/> key for the FMvsDialogParameters constructor. Resolved and logged, not
    /// called: the parameters are built here instead (<see cref="FMvsDialogParameters.Empty"/>).
    /// </summary>
    public const string DialogParametersCtorName = "FMvsDialogParameters::FMvsDialogParameters";
    /// <summary>The <see cref="GameFunctions"/> key for the function that binds a dialog button's delegate, which <see cref="AssignCallbackToButton"/> calls.</summary>
    public const string DialogCallbackSetterName = "SingleParamDialogCallbackSetter";
    /// <summary>The <see cref="GameFunctions"/> key for the game's quit callback, bound to the refund dialog's Disagree button.</summary>
    public const string QuitGameName = "QuitGame";
    /// <summary>The <see cref="GameFunctions"/> key for UMvsNotificationManager::Get, which takes the game instance.</summary>
    public const string GetNotificationManagerName = "UMvsNotificationManager::Get";
    /// <summary>The <see cref="GameFunctions"/> key for UMvsNotificationManager::RequestShowNotification, which shows a toast.</summary>
    public const string RequestShowNotificationName = "UMvsNotificationManager::RequestShowNotification";
    /// <summary>The <see cref="GameFunctions"/> key for the UFighterGameInstance constructor, whose tail jump is redirected to record each instance.</summary>
    public const string FighterGameInstanceCtorName = "UFighterGameInstance::UFighterGameInstance";

    private static nint s_fighterGameInstance;
    private static long s_fighterGameInstanceTick;

    /// <summary>The UFighterGameInstance the game constructed, captured by the FighterInstance hook; 0 until then.
    /// Written by a hook on a game thread and read from others, so the read is volatile.</summary>
    public static nint FighterGameInstance => Volatile.Read(ref s_fighterGameInstance);

    /// <summary>When the instance was last (re)constructed; the game builds several in its first seconds.</summary>
    public static long FighterGameInstanceTick => Volatile.Read(ref s_fighterGameInstanceTick);

    /// <summary>Called by the FighterInstance hook each time the game constructs an instance.</summary>
    public static void RecordFighterGameInstance(nint instance)
    {
        Volatile.Write(ref s_fighterGameInstanceTick, Environment.TickCount64);
        Volatile.Write(ref s_fighterGameInstance, instance);
    }

    /// <summary>
    /// The frontend manager, and whether it has a current state widget: the condition the C++
    /// client's ShowDialog required before touching the UI. Reads are guarded; nothing here
    /// calls into the game beyond the getter.
    /// </summary>
    public static (nint Frontend, nint StateWidget) FrontendState()
    {
        Engine.Require("FrontendState");
        var getFrontend = (delegate* unmanaged<nint, nint>)GameFunctions.Address(GetFrontendManagerName);
        if (getFrontend == null || FighterGameInstance == 0)
        {
            return (0, 0);
        }

        nint frontend = getFrontend(FighterGameInstance);
        if (frontend == 0 || !Memory.CodeWriter.TryRead(frontend + Mvs.FrontendCurrentStateWidget, out nint widget))
        {
            return (frontend, 0);
        }

        return (frontend, widget);
    }

    /// <summary>The game instance's UMvsNotificationManager, or 0 while the getter or the instance is not known.</summary>
    public static nint NotificationManager()
    {
        Engine.Require("NotificationManager");
        var get = (delegate* unmanaged<nint, nint>)GameFunctions.Address(GetNotificationManagerName);
        return get == null || FighterGameInstance == 0 ? 0 : get(FighterGameInstance);
    }

    /// <summary>Shows a toast. Returns the manager it went to, or 0 if there was none yet.</summary>
    public static nint ShowNotification(string? text, string? caption, float timeoutSeconds = 5.0f, bool autoDismissWhenClicked = true, bool setWidgetClass = false)
    {
        Engine.Require("ShowNotification");
        nint manager = NotificationManager();
        if (manager == 0)
        {
            return 0;
        }

        var request = (delegate* unmanaged<nint, FMvsNotificationData*, nint>)GameFunctions.Address(RequestShowNotificationName);
        if (request == null)
        {
            return 0;
        }

        var data = FMvsNotificationData.Empty();
        if (text != null)
        {
            data.Text = UE.MakeText(text);
        }

        if (caption != null)
        {
            data.Caption = UE.MakeText(caption);
        }

        data.TimeoutSeconds = timeoutSeconds;
        data.AutoDismissWhenClicked = autoDismissWhenClicked;
        data.Category = EMvsNotificationCategory.None;
        // A notification with no widget class is silently dropped by the manager when it is
        // asked from outside the game's own flow (the poller found this); the startup toast
        // never needed it.
        if (setWidgetClass)
        {
            if (!Memory.CodeWriter.TryRead(manager + Mvs.NotificationManagerDefaultWidgetClass, out nint widgetClass) || widgetClass == 0)
            {
                return 0;
            }

            data.WidgetClass = (ulong)widgetClass;
        }
        request(manager, &data);
        return manager;
    }

    /// <summary>Opens a dialog; returns the UMvsDialog, or 0 when the frontend is not ready.</summary>
    public static nint ShowDialog(string prompt, string? description = null, string? buttonOne = null, string? buttonTwo = null, string? buttonThree = null,
        int selectedButton = -1, bool showExitButton = false, bool showSpinner = false, bool showSolidBackground = true, bool hideActionBar = false)
    {
        Engine.Require("ShowDialog");
        var getFrontend = (delegate* unmanaged<nint, nint>)GameFunctions.Address(GetFrontendManagerName);
        var addDialog = (delegate* unmanaged<nint, FMvsDialogParameters*, nint>)GameFunctions.Address(AddDialogName);
        if (getFrontend == null || addDialog == null || FighterGameInstance == 0)
        {
            return 0;
        }

        nint frontend = getFrontend(FighterGameInstance);
        if (frontend == 0 || !Memory.CodeWriter.TryRead(frontend + Mvs.FrontendCurrentStateWidget, out nint stateWidget) || stateWidget == 0)
        {
            return 0;
        }

        var p = FMvsDialogParameters.Empty();
        p.PromptText = UE.MakeText(prompt);
        if (description != null)
        {
            p.PromptDescriptionText = UE.MakeText(description);
        }

        if (buttonOne != null)
        {
            p.ButtonOneText = UE.MakeText(buttonOne);
        }

        if (buttonTwo != null)
        {
            p.ButtonTwoText = UE.MakeText(buttonTwo);
        }

        if (buttonThree != null)
        {
            p.ButtonThreeText = UE.MakeText(buttonThree);
        }

        p.ButtonToFocus = selectedButton == -1 ? default : new DialogFocusButton { Value = selectedButton, IsSet = true };
        p.ShowExitButton = showExitButton;
        p.ShowSpinner = showSpinner;
        p.ShowSolidBackground = showSolidBackground;
        p.HideActionBar = hideActionBar;
        return addDialog(frontend, &p);
    }

    /// <summary>
    /// Makes a dialog button call <paramref name="mainCallback"/> (and <paramref name="cleanupCallback"/>
    /// when the delegate is destroyed). The game binds a single-parameter delegate to the button,
    /// then its delegate object's vtable is swapped for one of ours: slot 10 is the call, slot 0
    /// the cleanup, everything else a no-op. This is the C++ FakeVFTabler, and like it the table
    /// is never freed. Callbacks are UnmanagedCallersOnly functions taking the delegate as
    /// their only argument.
    /// </summary>
    public static ulong AssignCallbackToButton(nint dialog, int delegateOffset, nint mainCallback, nint cleanupCallback = 0)
    {
        Engine.Require("AssignCallbackToButton");
        var setter = (delegate* unmanaged<nint, ulong*, nint, nint*, void>)GameFunctions.Address(DialogCallbackSetterName);
        if (setter == null)
        {
            throw new InvalidOperationException("dialog callback setter not resolved");
        }

        nint button = dialog + delegateOffset;
        ulong result;
        nint self = dialog;
        setter(button, &result, dialog, &self);

        nint* table = (nint*)NativeMemory.Alloc(11 * (nuint)sizeof(nint));
        for (int i = 0; i < 11; i++)
        {
            table[i] = (nint)(delegate* unmanaged<void>)&Empty;
        }

        if (cleanupCallback != 0)
        {
            table[0] = cleanupCallback;
        }

        if (mainCallback != 0)
        {
            table[10] = mainCallback;
        }

        // InvocationList.Data[0].DelegateAllocator points at the delegate object; its first
        // qword is the vtable pointer.
        nint data = ((TMulticastDelegateBase*)button)->InvocationList.Data;
        nint allocator = ((FDelegateBase*)data)->DelegateAllocator;
        *(nint*)allocator = (nint)table;
        return result;
    }

    [UnmanagedCallersOnly]
    private static void Empty()
    {
    }
}
