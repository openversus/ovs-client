using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Net;

/// <summary>
/// One header for <see cref="RequestHeaders"/> to add to the game's requests to the OpenVersus server.
/// </summary>
/// <param name="Name">The header's name; an HTTP token, checked when the rule is added.</param>
/// <param name="Value">Read on every matching request; "" means nothing to send yet.</param>
/// <param name="Path">
/// Only requests to this path, relative to the configured server's own path ("/access" on
/// "https://host/" is "/access"; on "https://host/base/" it is "/base/access"); null for every request
/// to the server. The query string is not part of it.
/// </param>
/// <param name="OnlyIfMissing">Adds nothing when the request already has a header of this name, in any case.</param>
public sealed record HeaderRule(string Name, Func<string> Value, string? Path = null, bool OnlyIfMissing = false);

/// <summary>
/// Headers the client adds to the game's own HTTP requests, only to those whose host and port are a
/// configured OpenVersus server (<c>[Server.Game]</c> and <c>[Server.Prod]</c> <c>ServerUrl</c>), so
/// nothing added here reaches anyone else. It only adds headers: the URL, method and body stay as the
/// game set them. <see cref="Hooks.RequestHeadersHook"/> feeds it the two moments of the engine's curl
/// request setup: the URL (<see cref="NoteUrl"/>), then the header list (<see cref="AppendNoted"/>).
/// Values are never logged, only rule names and lengths.
/// </summary>
public sealed unsafe class RequestHeaders
{
    /// <summary>The header the game sends its Hydra session token in; the server reads the player from it on /access.</summary>
    public const string HydraAccessToken = "x-hydra-access-token";
    /// <summary>This install's identify token, on every request to the OpenVersus server.</summary>
    public const string OvsIdentity = "X-OVS-Identity";

    /// <summary>
    /// "1" on every request when the HydraZstd hook took on this build, so the server sends zstd sections only to a game
    /// that can read them: a build the hook's pattern misses (the Epic Games Store one, 2026-10-08) would inflate zstd as
    /// zlib and crash on the first big answer. Sent only when the hook took; never "0".
    /// </summary>
    public const string OvsZstd = "X-OVS-Zstd";

    /// <summary>
    /// The installed OVS_Experimental paks, "name=sha256" of each .utoc separated by semicolons (<see cref="PakUpdate.ExperimentalReport"/>):
    /// a queue whose mutators live in one (1v1 Testing Grounds) lets in only players with the exact pak, or the match
    /// would desync. Not sent when there is none.
    /// </summary>
    public const string OvsPaks = "X-OVS-Paks";

    private const int LoggedSendsPerRule = 3;
    private const int MaxRules = 64;

    // What the URL call noted for the header call that follows it on the same thread, in the same
    // request setup. Keyed by the curl handle, so a setup that returns between the two calls leaves
    // a note no later header call can match.
    [ThreadStatic] private static RequestHeaders? t_owner;
    [ThreadStatic] private static nint t_handle;
    [ThreadStatic] private static ulong t_selection;

    private readonly Uri[] _servers;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private Rule[] _rules = [];

    private sealed class Rule(HeaderRule definition, byte[] name)
    {
        public HeaderRule Definition { get; } = definition;
        public byte[] Name { get; } = name;
        public int Sent;
        public int Refused;
    }

    /// <summary>
    /// The servers whose requests get headers: each URL that parses (<see cref="Urls.Parse"/>);
    /// empty and duplicate ones are dropped.
    /// </summary>
    public RequestHeaders(IEnumerable<string> serverUrls, ILogger? log)
    {
        _servers = serverUrls.Select(Urls.Parse).OfType<Uri>().DistinctBy(u => (u.Host.ToLowerInvariant(), u.Port, BasePath(u))).ToArray();
        _log = log;
    }

    /// <summary>The servers requests are matched against.</summary>
    public IReadOnlyList<Uri> Servers => _servers;

    /// <summary>
    /// Adds a rule; it applies from the next request on. Throws an <see cref="ArgumentException"/> when
    /// the name is not an HTTP token or the path does not start with '/', and an
    /// <see cref="InvalidOperationException"/> past 64 rules.
    /// </summary>
    public void Add(HeaderRule rule)
    {
        if (!IsToken(rule.Name))
        {
            throw new ArgumentException($"\"{rule.Name}\" is not a header name", nameof(rule));
        }

        if (rule.Path != null && !rule.Path.StartsWith('/'))
        {
            throw new ArgumentException($"the path \"{rule.Path}\" of {rule.Name} does not start with '/'", nameof(rule));
        }

        lock (_lock)
        {
            if (_rules.Length == MaxRules)
            {
                throw new InvalidOperationException($"no room for {rule.Name}: {MaxRules} rules at most");
            }

            _rules = [.. _rules, new Rule(rule, Encoding.ASCII.GetBytes(rule.Name))];
        }
    }

    /// <summary>
    /// Which rules apply to <paramref name="url"/>, one bit per rule in the order added; 0 when the URL
    /// does not parse or is not on a configured server.
    /// </summary>
    public ulong Select(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return 0;
        }

        var rules = _rules;
        ulong selection = 0;
        foreach (var server in _servers)
        {
            if (!string.Equals(uri.Host, server.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != server.Port)
            {
                continue;
            }

            for (int i = 0; i < rules.Length; i++)
            {
                string? path = rules[i].Definition.Path;
                if (path == null || uri.AbsolutePath == BasePath(server) + path)
                {
                    selection |= 1UL << i;
                }
            }
        }

        return selection;
    }

    /// <summary>Notes the request <paramref name="handle"/> is being set up for, on this thread, for <see cref="AppendNoted"/>.</summary>
    public void NoteUrl(nint handle, string url)
    {
        t_owner = null;
        t_selection = Select(url);
        t_handle = handle;
        t_owner = this;
    }

    /// <summary>
    /// Appends the headers <see cref="NoteUrl"/> selected for <paramref name="handle"/> on this thread to
    /// <paramref name="list"/>, and clears the note. Nothing when the note is for another handle or there
    /// is none. Returns how many were appended.
    /// </summary>
    public int AppendNoted(nint handle, nint list, delegate* unmanaged<nint, byte*, nint> slistAppend)
    {
        bool mine = t_owner == this && t_handle == handle;
        ulong selection = t_selection;
        t_owner = null;
        t_handle = 0;
        t_selection = 0;
        return mine ? Append(list, selection, slistAppend) : 0;
    }

    /// <summary>
    /// Appends each selected rule's header to the non-empty curl header <paramref name="list"/> through
    /// <paramref name="slistAppend"/> (curl_slist_append, which copies the text and, on a non-empty list,
    /// returns the same head). The nodes are curl's: the game's own curl_slist_free_all frees them with
    /// the rest. Returns how many were appended; nothing when the list is null.
    /// </summary>
    public int Append(nint list, ulong selection, delegate* unmanaged<nint, byte*, nint> slistAppend)
    {
        if (list == 0 || selection == 0)
        {
            return 0;
        }

        var rules = _rules;
        int appended = 0;
        for (int i = 0; i < rules.Length; i++)
        {
            if ((selection & (1UL << i)) == 0)
            {
                continue;
            }

            var rule = rules[i];
            if (rule.Definition.OnlyIfMissing && Has(list, rule.Name))
            {
                continue;
            }

            string value = rule.Definition.Value() ?? "";
            if (value.Length == 0)
            {
                continue;
            }

            if (!IsValue(value))
            {
                if (Interlocked.Increment(ref rule.Refused) == 1)
                {
                    _log?.Warn($"[RequestHeaders] {rule.Definition.Name}: the value ({value.Length} characters) is not printable ASCII; not sent");
                }

                continue;
            }

            byte[] line = new byte[rule.Name.Length + 2 + value.Length + 1];
            rule.Name.CopyTo(line, 0);
            line[rule.Name.Length] = (byte)':';
            line[rule.Name.Length + 1] = (byte)' ';
            Encoding.ASCII.GetBytes(value, 0, value.Length, line, rule.Name.Length + 2);
            nint head;
            fixed (byte* text = line)
            {
                head = slistAppend(list, text);
            }

            if (head == 0)
            {
                _log?.Warn($"[RequestHeaders] {rule.Definition.Name}: curl could not add it");
                continue;
            }

            appended++;
            if (Interlocked.Increment(ref rule.Sent) <= LoggedSendsPerRule)
            {
                _log?.Info($"[RequestHeaders] Sent {rule.Definition.Name} ({value.Length} characters)");
            }
        }

        return appended;
    }

    /// <summary>Whether the curl header list has a header named <paramref name="name"/>, in any case: a node reading "Name:" or "Name;".</summary>
    internal static bool Has(nint list, ReadOnlySpan<byte> name)
    {
        // struct curl_slist { char *data; struct curl_slist *next; }
        for (nint node = list; node != 0; node = *(nint*)(node + sizeof(nint)))
        {
            byte* data = *(byte**)node;
            if (data != null && StartsWithName(data, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithName(byte* data, ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (data[i] == 0 || char.ToLowerInvariant((char)data[i]) != char.ToLowerInvariant((char)name[i]))
            {
                return false;
            }
        }

        return data[name.Length] is (byte)':' or (byte)';';
    }

    private static string BasePath(Uri server) => server.AbsolutePath.TrimEnd('/');

    /// <summary>An HTTP token (RFC 9110 tchar): what a header name may be.</summary>
    internal static bool IsToken(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    /// <summary>Printable ASCII, spaces allowed, nothing else: no CR, LF or other control characters.</summary>
    internal static bool IsValue(string value) => value.All(c => c is >= ' ' and <= '~');
}
