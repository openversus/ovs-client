using System.Runtime.InteropServices;
using System.Text.Json;
using OpenVersus.Net;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Game;

/// <summary>
/// The main menu's server panel (Tuggernuts, 2026-10-21, after FDAVersus's "PLAYERS ONLINE / IN QUEUE" box; 2026-10-27: every
/// open queue on its own line): players online, then how many are playing and searching in 1v1, 2v2, 1v1 Testing Grounds, the
/// event slot (2v2 Testing Grounds, or FFA on its days) and Arenas while it is on, and whether Twosday is on (1v1 is off then).
/// The panel is the experimental pak's WBP_OVS_ServerStatus; what a Blueprint cannot do is here: reading the server's numbers (GET /api/matches, the website's
/// live-matches feed, every <see cref="FetchEvery"/>), loading the widget class from the pak, putting it on screen on the
/// main menu (<see cref="HomeScreen"/>) and passing it the numbers (its OVS_SetStatus formats them). Anywhere else it is
/// hidden, and nothing is fetched: only a game on the main menu asks (every <see cref="FetchEvery"/>, and at once on
/// arriving there; the server rebuilds the numbers at most every 2 s). Anything that fails leaves the panel off and is
/// logged once; it never touches gameplay.
/// </summary>
public sealed unsafe class ServerStatusPanel(string serverUrl, IHttpTransport http, ObjectFinder finder, GameImage image, ILogger log)
{
    /// <summary>The widget class in the experimental pak.</summary>
    public const string WidgetClassPath = "/OVSExperimental/UI/WBP_OVS_ServerStatus.WBP_OVS_ServerStatus_C";
    /// <summary>The main menu's screen (UMvsFrontendManager::GetCurrentScreenId), the only one the panel shows on.</summary>
    public const string HomeScreen = "ScreenId.Frontend.Home";
    private static readonly TimeSpan FetchEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);
    // ESlateVisibility: Collapsed, and HitTestInvisible (drawn, never takes the mouse; nor do its children).
    private const byte Collapsed = 1, HitTestInvisible = 3;

    private CancellationTokenSource? _stopping;
    private volatile Status? _status;
    // Whether the frontend showed at the last update (game thread writes, the loop reads): fetch only then.
    private volatile bool _onFrontend;
    private nint _class;
    private nint _widget;
    private GameFunction? _setStatus, _create, _addToViewport, _isInViewport, _setVisibility;
    private nint _widgetLibrary;
    private nint _lastState;
    private string _lastScreen = "";
    private GameFunction? _screenId;
    // Set on the game thread (Fail), read by the loop: once set, nothing is fetched or posted again.
    private volatile bool _failed;
    // Creates in a row that returned no widget (EnsureWidget): after a logout the class may have gone with the old world.
    private int _createFailures;
    private const int MaxCreateFailures = 5;
    private const string WidgetClassName = "WBP_OVS_ServerStatus_C";
    private readonly HashSet<string> _logged = [];

    // OVS_SetStatus's inputs by name, lowercased (the engine may keep another spelling: Update), ints and bools apart.
    private sealed record Status(IReadOnlyDictionary<string, int> Numbers, IReadOnlyDictionary<string, bool> Flags);

    /// <summary>Starts the fetch-and-update thread.</summary>
    public void Start()
    {
        if (_stopping != null || Urls.Join(serverUrl, "/api/matches") is not { } url)
        {
            return;
        }

        _stopping = new CancellationTokenSource();
        CancellationToken token = _stopping.Token;
        new Thread(() => Loop(url, token)) { IsBackground = true, Name = "OVS server panel" }.Start();
        log.Info("[ServerPanel] Started");
    }

    /// <summary>Tells the thread to stop.</summary>
    public void Stop() => _stopping?.Cancel();

    private void Loop(Uri url, CancellationToken token)
    {
        var lastFetch = DateTime.MinValue;
        bool wasOnFrontend = false;
        // Ends when the panel is off for good (Fail): _onFrontend is only ever written by Update, which no longer runs
        // then, so a loop that went on would have kept fetching every FetchEvery for the rest of the session, in matches
        // too, with nothing to show the numbers on.
        while (!token.IsCancellationRequested && !_failed)
        {
            bool onFrontend = _onFrontend;
            if (onFrontend && (!wasOnFrontend || DateTime.UtcNow - lastFetch >= FetchEvery))
            {
                lastFetch = DateTime.UtcNow;
                Fetch(url);
            }

            wasOnFrontend = onFrontend;

            if (Engine.IsUp)
            {
                GameThread.Post("ServerPanel", () => Hooking.HookGuard.Run("ServerPanel", this, static p => p.Update()));
            }

            token.WaitHandle.WaitOne(TickEvery);
        }

        if (_failed)
        {
            log.Info("[ServerPanel] stopped: the server is not asked again this session");
        }
    }

    private void Fetch(Uri url)
    {
        var result = http.Get(url, TimeSpan.FromSeconds(5));
        if (!result.Ok)
        {
            LogOnce("fetch", $"[ServerPanel] /api/matches: {result.Error ?? $"HTTP {result.Status}"} (logged once)");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            _status = StatusOf(doc.RootElement);
        }
        catch (JsonException e)
        {
            LogOnce("parse", $"[ServerPanel] /api/matches is not the expected JSON: {e.Message} (logged once)");
        }
    }

    // The panel's inputs from the feed: each queue's searching and playing (a server without "playing" leaves them 0), the event
    // slot FFA while its queue is open, else 2v2 Testing Grounds (during Twosday neither, nor 1v1 Testing Grounds).
    private static Status StatusOf(JsonElement root)
    {
        int Count(string list, string queue) => root.TryGetProperty(list, out var s) && s.TryGetProperty(queue, out var n) && n.TryGetInt32(out int v) ? v : 0;
        bool Flag(string name) => root.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.True;
        bool ffaOpen = Flag("ffaOpen");
        string eventQueue = ffaOpen ? "FFA" : "2v2 Testing Grounds";
        var numbers = new Dictionary<string, int>
        {
            ["online"] = root.TryGetProperty("onlinePlayers", out var o) && o.TryGetInt32(out int on) ? on : 0,
            ["queue1v1"] = Count("searching", "1v1"), ["playing1v1"] = Count("playing", "1v1"),
            ["queue2v2"] = Count("searching", "2v2"), ["playing2v2"] = Count("playing", "2v2"),
            ["queuetg1"] = Count("searching", "1v1 Testing Grounds"), ["playingtg1"] = Count("playing", "1v1 Testing Grounds"),
            ["queueevent"] = Count("searching", eventQueue), ["playingevent"] = Count("playing", eventQueue),
            ["queuearena"] = Count("searching", "Arena"), ["playingarena"] = Count("playing", "Arena"),
        };
        var flags = new Dictionary<string, bool>
        {
            ["eventisffa"] = ffaOpen,
            ["eventopen"] = ffaOpen || Flag("testingGrounds2v2Open"),
            ["tg1open"] = Flag("testingGroundsOpen"),
            ["arenaopen"] = Flag("arenaOpen"),
            ["twosday"] = Flag("twosday"),
        };
        return new Status(numbers, flags);
    }

    // Game thread: on the frontend, the panel on screen with the latest numbers; elsewhere, hidden.
    private void Update()
    {
        var (frontend, state) = GameUi.FrontendState();
        if (state != _lastState)
        {
            _lastState = state;
            if (state != 0 && ClassName(state) is { } screen)
            {
                LogOnce("state:" + screen, $"[ServerPanel] frontend state widget {screen}");
            }
        }

        // The screen the frontend shows (UMvsFrontendManager::GetCurrentScreenId, a gameplay tag), logged on every change, for
        // choosing where the panel shows.
        string screenId = ScreenId(frontend);
        if (screenId != _lastScreen)
        {
            _lastScreen = screenId;
            log.Info($"[ServerPanel] screen {(screenId.Length > 0 ? screenId : "(none)")}");
        }

        // Only the main menu: every other screen and every match hide it, and only the main menu fetches.
        _onFrontend = state != 0 && screenId == HomeScreen;
        if (!_onFrontend || _status is not { } status)
        {
            Hide();
            return;
        }

        if (!EnsureWidget(state))
        {
            return;
        }

        SetVisibility(HitTestInvisible);
        Span<byte> parameters = stackalloc byte[_setStatus!.Signature.Reflected!.ParmsSize];
        parameters.Clear();
        // Engine names ignore case and keep the spelling registered first: on a PC where something else in the game made
        // "online" before the panel loaded, the parameter reads back as "online", so its count was never written (the
        // panel showed 0 online while the queues, whose names nothing else uses, were right). Matched in any capitals.
        // An input the widget has and the feed does not stays 0 (an older pak's panel takes only the inputs it has).
        foreach (var p in _setStatus.Signature.Reflected.Parameters)
        {
            string key = p.Name.ToLowerInvariant();
            if (status.Numbers.TryGetValue(key, out int number))
            {
                BitConverter.TryWriteBytes(parameters[p.Offset..], number);
            }
            else if (status.Flags.TryGetValue(key, out bool flag))
            {
                parameters[p.Offset] = (byte)(flag ? 1 : 0);
            }
        }

        Reflection.Invoke(_setStatus, _widget, parameters);
    }

    private void Hide()
    {
        if (_widget != 0 && finder.IsLive(_widget))
        {
            SetVisibility(Collapsed);
        }
    }

    // The widget on screen: the class loaded from the pak once, a widget created and added to the viewport whenever there is
    // none (a level change removes viewport widgets). False, logged once, when something is missing: the panel stays off.
    private bool EnsureWidget(nint worldContext)
    {
        if (_widget != 0 && finder.IsLive(_widget) && InViewport())
        {
            return true;
        }

        _widget = 0;
        // The class is loaded, not kept: a logout (the game's travel back to its login map) can collect it and give its
        // slot to another object, so a pointer that is live but no longer our class is loaded again.
        if (_class == 0 || !finder.IsLive(_class) || ObjectName(_class) != WidgetClassName)
        {
            _class = LoadClass(WidgetClassPath);
            if (_class == 0)
            {
                Fail($"the widget class {WidgetClassPath} did not load (is the experimental pak installed?)");
                return false;
            }

            nint function = finder.FindFunction(_class, "OVS_SetStatus");
            _setStatus = function == 0 ? null : Reflected(function, "WBP_OVS_ServerStatus_C", "OVS_SetStatus");
            _create = Reflection.Find(finder, image, "WidgetBlueprintLibrary", "Create");
            _addToViewport = Reflection.Find(finder, image, "UserWidget", "AddToViewport");
            _isInViewport = Reflection.Find(finder, image, "Widget", "IsInViewport");
            _setVisibility = Reflection.Find(finder, image, "Widget", "SetVisibility");
            _widgetLibrary = finder.FindDefaultObject(finder.FindClass("WidgetBlueprintLibrary"));
            if (_setStatus == null || _create?.Signature.Reflected == null || _addToViewport == null || _isInViewport == null || _setVisibility == null || _widgetLibrary == 0)
            {
                var missing = new List<string>();
                if (_setStatus == null)
                { missing.Add($"OVS_SetStatus (function 0x{function:X})"); }
                if (_create?.Signature.Reflected == null)
                { missing.Add("WidgetBlueprintLibrary::Create"); }
                if (_addToViewport == null)
                { missing.Add("UserWidget::AddToViewport"); }
                if (_isInViewport == null)
                { missing.Add("Widget::IsInViewport"); }
                if (_setVisibility == null)
                { missing.Add("Widget::SetVisibility"); }
                if (_widgetLibrary == 0)
                { missing.Add("the WidgetBlueprintLibrary default object"); }
                Fail("not found: " + string.Join(", ", missing));
                return false;
            }
        }

        // UWidgetBlueprintLibrary::Create(WorldContextObject, WidgetType, OwningPlayer): the frontend screen is the world context.
        var create = _create!.Signature.Reflected!;
        Span<byte> parameters = stackalloc byte[create.ParmsSize];
        parameters.Clear();
        foreach (var p in create.Parameters)
        {
            if (string.Equals(p.Name, "WorldContextObject", StringComparison.OrdinalIgnoreCase))
            {
                BitConverter.TryWriteBytes(parameters[p.Offset..], (long)worldContext);
            }
            else if (string.Equals(p.Name, "WidgetType", StringComparison.OrdinalIgnoreCase))
            {
                BitConverter.TryWriteBytes(parameters[p.Offset..], (long)_class);
            }
        }

        Reflection.Invoke(_create, _widgetLibrary, parameters);
        _widget = create.ReturnValueOffset >= 0 ? (nint)BitConverter.ToInt64(parameters[create.ReturnValueOffset..]) : 0;
        if (_widget == 0)
        {
            // Seen 2026-10-27 after a disconnect and a new login: the panel stayed off for the session. Try again with the
            // class loaded afresh (the next update), and only give up after MaxCreateFailures in a row.
            _createFailures++;
            log.Warn($"[ServerPanel] WidgetBlueprintLibrary::Create returned no widget ({_createFailures}/{MaxCreateFailures}): " +
                $"class 0x{_class:X} {ObjectName(_class) ?? "(unreadable)"}, world context 0x{worldContext:X} {ClassName(worldContext) ?? "(unreadable)"}");
            _class = 0;
            if (_createFailures >= MaxCreateFailures)
            {
                Fail("WidgetBlueprintLibrary::Create returned no widget");
            }

            return false;
        }

        _createFailures = 0;

        Span<byte> z = stackalloc byte[Math.Max(_addToViewport!.Signature.Reflected!.ParmsSize, 4)];
        z.Clear();
        BitConverter.TryWriteBytes(z, 5);
        Reflection.Invoke(_addToViewport, _widget, z[.._addToViewport.Signature.Reflected.ParmsSize]);
        log.Info("[ServerPanel] panel on screen");
        return true;
    }

    private bool InViewport()
    {
        var sig = _isInViewport?.Signature.Reflected;
        if (sig is not { ReturnValueOffset: >= 0 })
        {
            return false;
        }

        Span<byte> parameters = stackalloc byte[sig.ParmsSize];
        parameters.Clear();
        Reflection.Invoke(_isInViewport!, _widget, parameters);
        return parameters[sig.ReturnValueOffset] != 0;
    }

    private void SetVisibility(byte visibility)
    {
        if (_setVisibility?.Signature.Reflected is not { ParmsSize: >= 1 } sig)
        {
            return;
        }

        Span<byte> parameters = stackalloc byte[sig.ParmsSize];
        parameters.Clear();
        parameters[0] = visibility;
        Reflection.Invoke(_setVisibility, _widget, parameters);
    }

    // A class by its path, as Blueprints load one: KismetSystemLibrary MakeSoftClassPath(path), Conv_SoftClassPathToSoftClassRef,
    // LoadClassAsset_Blocking. Each step's return value is the next step's only input. 0 when any step is missing or fails.
    private nint LoadClass(string path)
    {
        nint library = finder.FindDefaultObject(finder.FindClass("KismetSystemLibrary"));
        var make = Reflection.Find(finder, image, "KismetSystemLibrary", "MakeSoftClassPath");
        var conv = Reflection.Find(finder, image, "KismetSystemLibrary", "Conv_SoftClassPathToSoftClassRef");
        var load = Reflection.Find(finder, image, "KismetSystemLibrary", "LoadClassAsset_Blocking");
        if (library == 0 || make?.Signature.Reflected is not { } m || conv?.Signature.Reflected is not { } c || load?.Signature.Reflected is not { } l
            || m.ReturnValueOffset < 0 || c.ReturnValueOffset < 0 || l.ReturnValueOffset < 0)
        {
            return 0;
        }

        // The FString argument: our own characters, alive for the process (the engine copies what it keeps).
        nint chars = Marshal.StringToHGlobalUni(path);
        var text = new FString { Data = (char*)chars, Count = path.Length + 1, Max = path.Length + 1 };
        Span<byte> first = stackalloc byte[m.ParmsSize];
        first.Clear();
        int input = m.Parameters.FirstOrDefault(p => !p.IsReturn)?.Offset ?? 0;
        MemoryMarshal.Write(first[input..], in text);
        Reflection.Invoke(make, library, first);

        int softSize = m.ParmsSize - m.ReturnValueOffset;
        Span<byte> second = stackalloc byte[c.ParmsSize];
        second.Clear();
        first.Slice(m.ReturnValueOffset, Math.Min(softSize, c.ReturnValueOffset)).CopyTo(second);
        Reflection.Invoke(conv, library, second);

        Span<byte> third = stackalloc byte[l.ParmsSize];
        third.Clear();
        second.Slice(c.ReturnValueOffset, Math.Min(c.ParmsSize - c.ReturnValueOffset, l.ReturnValueOffset)).CopyTo(third);
        Reflection.Invoke(load, library, third);
        nint loaded = (nint)BitConverter.ToInt64(third[l.ReturnValueOffset..]);
        log.Info($"[ServerPanel] {path}: {(loaded != 0 ? "loaded" : "not loaded")}");
        return loaded;
    }

    private GameFunction Reflected(nint ufunction, string className, string functionName) => new()
    {
        Name = $"{className}::{functionName}",
        Address = ufunction,
        Source = FunctionSource.Reflected,
        Image = image,
        Origin = "loaded class",
        Signature = new FunctionSignature { Reflected = Reflection.Describe(finder.Memory, finder.Names, ufunction, className, functionName, _class) },
    };

    // UMvsFrontendManager::GetCurrentScreenId(): FGameplayTag (its FName TagName) as text; "" when it cannot be asked.
    private string ScreenId(nint frontend)
    {
        if (frontend == 0)
        {
            return "";
        }

        _screenId ??= Reflection.Find(finder, image, "MvsFrontendManager", "GetCurrentScreenId");
        if (_screenId?.Signature.Reflected is not { ReturnValueOffset: >= 0 } sig)
        {
            return "";
        }

        Span<byte> parameters = stackalloc byte[Math.Max(sig.ParmsSize, 8)];
        parameters.Clear();
        Reflection.Invoke(_screenId, frontend, parameters[..sig.ParmsSize]);
        var name = MemoryMarshal.Read<FName>(parameters[sig.ReturnValueOffset..]);
        return name.Index == 0 ? "" : finder.Names.ToString(name) ?? "";
    }

    private string? ObjectName(nint obj) =>
        ObjectHeader.TryRead(finder.Memory, obj, out var header) ? finder.Names.ToString(header.Name) : null;

    private string? ClassName(nint obj) =>
        ObjectHeader.TryRead(finder.Memory, obj, out var header) && ObjectHeader.TryRead(finder.Memory, header.ClassPrivate, out var cls)
            ? finder.Names.ToString(cls.Name)
            : null;

    private void Fail(string why)
    {
        _failed = true;
        log.Warn($"[ServerPanel] off: {why}");
    }

    private void LogOnce(string key, string line)
    {
        lock (_logged)
        {
            if (!_logged.Add(key))
            {
                return;
            }
        }

        log.Info(line);
    }
}
