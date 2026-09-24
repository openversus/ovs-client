using OpenVersus.Game;

namespace OpenVersus.Hooks;

/// <summary>Resolves the notification manager and its request function; the C++ NotificationHooks.</summary>
public static class NotificationHooks
{
    public static bool Apply(HookContext c)
    {
        // The C++ set the manager getter from a fixed address first, so a failed pattern still
        // left something usable. Same here: the RVA is the fallback, the pattern overrides it.
        GameFunctions.FromRva(GameUi.GetNotificationManagerName, GameFunctions.GetNotificationManagerRva, "UMvsNotificationManager* UMvsNotificationManager::Get(const UFighterGameInstance* context)", c.Image);

        c.Log.Info("==Notification Funcs==");
        if (!c.Status.UeFuncs)
        {
            c.Log.Error("UE Funcs were not enabled therefore Notifications cannot be used!");
            return false;
        }
        // This pattern matches three identical functions; the first is as good as any.
        var hit = c.Patterns.Find("Notifications");
        if (!hit.Found)
        {
            return false;
        }

        GameFunctions.FromCallSite(GameUi.RequestShowNotificationName, hit.Address + 32, "call at Notifications+32", "UMvsNotificationHandle* UMvsNotificationManager::RequestShowNotification(UMvsNotificationManager* this, const FMvsNotificationData* data)", c.Image);
        GameFunctions.FromCallSite(GameUi.GetNotificationManagerName, hit.Address + 19, "call at Notifications+19", "UMvsNotificationManager* UMvsNotificationManager::Get(const UFighterGameInstance* context)", c.Image);

        c.Log.Debug(GameFunctions.Find(GameUi.RequestShowNotificationName)!.ToString());
        c.Log.Debug(GameFunctions.Find(GameUi.GetNotificationManagerName)!.ToString());
        c.Log.Success("Notifications Usable!");
        return true;
    }
}
