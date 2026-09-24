using System.Text.Json;
using OpenVersus.Game;

namespace OpenVersus.Net;

/// <summary>
/// Polls /ovs/notifications every two seconds and acts on what comes back: a banner in the
/// game's own notification widget, and for match_cancel, a walk of the pre-match state machine
/// to send the player back to the lobby the way the native timeout would. Anything that
/// touches the engine is handed to the game thread.
/// </summary>
public sealed class NotificationPoller(string serverUrl, IHttpTransport http, ObjectFinder finder, GameImage image, Log log)
{
    private volatile bool _running;
    private Thread? _thread;

    public void Start()
    {
        if (_running)
        {
            log.Info("[NotifPoller] Already running");
            return;
        }
        if (Urls.Join(serverUrl, "/ovs/notifications") is not { } url)
        {
            log.Warn($"[NotifPoller] Failed to parse server URL: {serverUrl}");
            return;
        }
        _running = true;
        _thread = new Thread(() => Loop(url)) { IsBackground = true, Name = "OVS notification poller" };
        _thread.Start();
        log.Info("[NotifPoller] Started");
    }

    public void Stop() => _running = false;

    private void Loop(Uri url)
    {
        Thread.Sleep(8000); // let the hooks finish before polling
        log.Info($"[NotifPoller] Polling {url} every 2s");
        int failures = 0;
        while (_running)
        {
            var result = http.Get(url, TimeSpan.FromSeconds(2));
            if (!result.Ok)
            {
                failures++;
                if (failures == 1 || failures % 30 == 0)
                {
                    log.Warn($"[NotifPoller] Poll failed (count={failures}): {result.Error ?? $"HTTP {result.Status}"}");
                }
            }
            else
            {
                failures = 0;
                foreach (var n in Parse(result.Text))
                {
                    Dispatch(n);
                }
            }
            for (int i = 0; i < 20 && _running; i++)
            {
                Thread.Sleep(100);
            }
        }
        log.Info("[NotifPoller] Thread exiting");
    }

    public sealed record Notification(string Type, string Title, string Message, double? Timeout);

    public static List<Notification> Parse(string json)
    {
        var list = new List<Notification>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                list.Add(new Notification(type.GetString()!,
                    e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "",
                    e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "",
                    e.TryGetProperty("timeout", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetDouble() : null));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private void Dispatch(Notification n)
    {
        switch (n.Type)
        {
            case "match_cancel":
                log.Info($"[NotifPoller] Match cancel received: {n.Title} — {n.Message}");
                ShowBanner("Match Cancelled", "Opponent left the match", 5.0f);
                GameThread.Post("CancelMatch", CancelMatchOnGameThread);
                break;
            case "toast_received":
                log.Info($"[NotifPoller] Toast received: {n.Message}");
                ShowBanner(n.Title, n.Message, 4.0f);
                break;
            case "admin_banner":
                ShowBanner(n.Title, n.Message, (float)(n.Timeout ?? 8.0));
                break;
            default:
                log.Info($"[NotifPoller] Unknown notification type '{n.Type}' — ignoring");
                break;
        }
    }

    private void ShowBanner(string title, string message, float timeoutSeconds)
    {
        log.Info($"[NotifPoller] Banner queued: top='{title}' bottom='{message}' timeout={timeoutSeconds:F1}s");
        GameThread.Post("Banner", () =>
        {
            nint manager = GameUi.ShowNotification(title.Length > 0 ? title : null, message.Length > 0 ? message : null,
                timeoutSeconds > 0 ? timeoutSeconds : 8.0f, autoDismissWhenClicked: true, setWidgetClass: true);
            log.Info(manager != 0 ? "[NotifPoller] Banner shown on game thread" : "[NotifPoller] Banner: notification manager unavailable");
        });
    }

    /// <summary>
    /// If the player is still in the pre-match flow, invoke the transition-failed handler on
    /// the current state so the game returns to the lobby. In active gameplay there is no
    /// pre-match game state, and nothing is done.
    /// </summary>
    private void CancelMatchOnGameThread()
    {
        nint preMatchClass = finder.FindClass("MvsPreMatchGameState");
        nint preMatch = preMatchClass == 0 ? 0 : finder.FindInstanceOfClass(preMatchClass);
        if (preMatch == 0)
        {
            log.Info("[CancelMatch] No AMvsPreMatchGameState — player is in active gameplay, ignoring");
            return;
        }

        if (!Memory.CodeWriter.TryRead(preMatch + 0x320, out nint stateMachine) || !Plausible(stateMachine))
        {
            log.Info($"[CancelMatch] StateMachine ptr invalid (0x{stateMachine:X})");
            return;
        }
        if (!Memory.CodeWriter.TryRead(stateMachine + 0x58, out nint currentState) || !Plausible(currentState))
        {
            log.Info($"[CancelMatch] CurrentState ptr invalid (0x{currentState:X}) — no-op");
            return;
        }
        string className = ObjectHeader.TryRead(currentState, out var h) && ObjectHeader.TryRead(h.ClassPrivate, out var cls) ? UE.NameToString(cls.Name) ?? "?" : "?";
        log.Info($"[CancelMatch] CurrentState=0x{currentState:X} class='{className}'");

        var handler = Reflection.Find(finder, image, "MvsPreMatchTransitioningToGameplayState", "HandleTransitionToGameplayFailed");
        if (handler == null)
        {
            log.Info("[CancelMatch] UFunction HandleTransitionToGameplayFailed on MvsPreMatchTransitioningToGameplayState not found — no-op");
            return;
        }
        log.Info($"[CancelMatch] Invoking {handler.Signature} via ProcessEvent on state 0x{currentState:X}");
        Reflection.Invoke(handler, currentState, Span<byte>.Empty);
        log.Info("[CancelMatch] ProcessEvent returned cleanly — player should be transitioning");
    }

    private static bool Plausible(nint p) => p >= 0x10000 && p <= 0x00007FFFFFFFFFFF;
}
