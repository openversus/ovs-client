using System.Runtime.InteropServices;
using System.Text;
using OpenVersus.Net;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="RequestHeaders"/> without the game: which requests get which headers, and what it does to
/// a curl header list, through a stand-in curl_slist_append that copies the text into nodes of its own
/// and counts them, the way curl does. Redirecting the two calls in the game is proven in the game
/// (the log names both sites).
/// </summary>
[Collection("RequestHeaders")]
public unsafe class RequestHeadersTests : IDisposable
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJpbnN0YWxsSWQiOiJ4In0.c2lnbmF0dXJl";
    private const string Game = "https://ovs.example.net/";
    private const string Prod = "https://prod.ovs.example.net";

    private static readonly List<nint> s_allocations = [];
    private static int s_appends;

    private readonly ListLogger _log = new();
    private string _token = Token;

    public RequestHeadersTests()
    {
        s_appends = 0;
    }

    public void Dispose()
    {
        foreach (nint p in s_allocations)
        {
            NativeMemory.Free((void*)p);
        }

        s_allocations.Clear();
    }

    /// <summary>curl_slist_append: copies the text into a new 16-byte node, links it last, returns the head.</summary>
    [UnmanagedCallersOnly]
    private static nint FakeAppend(nint list, byte* text)
    {
        s_appends++;
        int length = 0;
        while (text[length] != 0)
        {
            length++;
        }

        var copy = (byte*)NativeMemory.Alloc((nuint)length + 1);
        new ReadOnlySpan<byte>(text, length + 1).CopyTo(new Span<byte>(copy, length + 1));
        var node = (nint*)NativeMemory.AllocZeroed(16);
        s_allocations.Add((nint)copy);
        s_allocations.Add((nint)node);
        node[0] = (nint)copy;
        if (list == 0)
        {
            return (nint)node;
        }

        nint last = list;
        while (*(nint*)(last + 8) != 0)
        {
            last = *(nint*)(last + 8);
        }

        *(nint*)(last + 8) = (nint)node;
        return list;
    }

    /// <summary>curl_slist_append out of memory.</summary>
    [UnmanagedCallersOnly]
    private static nint FailingAppend(nint list, byte* text) => 0;

    private static delegate* unmanaged<nint, byte*, nint> Append => &FakeAppend;

    /// <summary>A header list as the game builds one, before ours.</summary>
    private static nint GameList(params string[] headers)
    {
        nint list = 0;
        foreach (string h in headers)
        {
            fixed (byte* p = Encoding.ASCII.GetBytes(h + "\0"))
            {
                list = FakeAppendManaged(list, p);
            }
        }

        s_appends = 0;
        return list;
    }

    private static nint FakeAppendManaged(nint list, byte* text) => Append(list, text);

    private static List<string> Read(nint list)
    {
        var lines = new List<string>();
        for (nint node = list; node != 0; node = *(nint*)(node + 8))
        {
            lines.Add(Marshal.PtrToStringAnsi(*(nint*)node)!);
        }

        return lines;
    }

    /// <summary>The two rules the client adds, against the default two servers.</summary>
    private RequestHeaders Client(params string[] servers)
    {
        var h = new RequestHeaders(servers.Length > 0 ? servers : [Game, Prod], _log);
        h.Add(new HeaderRule(RequestHeaders.HydraAccessToken, () => _token, Path: "/access", OnlyIfMissing: true));
        h.Add(new HeaderRule(RequestHeaders.OvsIdentity, () => _token));
        return h;
    }

    private List<string> Send(RequestHeaders h, string url, params string[] gameHeaders)
    {
        nint list = GameList(gameHeaders.Length > 0 ? gameHeaders : ["User-Agent: MultiVersus/++UE5"]);
        h.NoteUrl(7, url);
        h.AppendNoted(7, list, Append);
        return Read(list);
    }

    [Fact]
    public void TheLoginGetsBothHeadersAndOtherRequestsOnlyTheIdentity()
    {
        var h = Client();
        Assert.Equal(["User-Agent: MultiVersus/++UE5", $"x-hydra-access-token: {Token}", $"X-OVS-Identity: {Token}"], Send(h, Prod + "/access"));
        Assert.Equal(["User-Agent: MultiVersus/++UE5", $"X-OVS-Identity: {Token}"], Send(h, Prod + "/profiles/me"));
        Assert.Equal(["User-Agent: MultiVersus/++UE5", $"X-OVS-Identity: {Token}"], Send(h, Game + "access-token?x=/access"));
        Assert.Equal(["User-Agent: MultiVersus/++UE5", $"x-hydra-access-token: {Token}", $"X-OVS-Identity: {Token}"], Send(h, "https://OVS.example.net/access?ticket=1"));
    }

    [Fact]
    public void OtherHostsAndPortsGetNothing()
    {
        var h = Client();
        foreach (string url in new[]
        {
            "https://event.wbinsights.com/access",
            "https://qos.multiplay.com/x",
            "https://ovs.example.net.evil.test/access",
            "https://evil.test/ovs.example.net/access",
            "https://ovs.example.net:8443/access",
            "http://ovs.example.net/access",
            "ftp://ovs.example.net:443/access",
            "not a url",
            "",
        })
        {
            Assert.Equal(["User-Agent: MultiVersus/++UE5"], Send(h, url));
        }

        Assert.Equal(0, s_appends);
    }

    [Fact]
    public void ThePathIsRelativeToTheServersOwnPath()
    {
        foreach (string server in new[] { "https://host.test/base", "https://host.test/base/" })
        {
            var h = Client(server);
            Assert.Contains($"x-hydra-access-token: {Token}", Send(h, "https://host.test/base/access"));
            Assert.DoesNotContain($"x-hydra-access-token: {Token}", Send(h, "https://host.test/access"));
            Assert.Contains($"X-OVS-Identity: {Token}", Send(h, "https://host.test/access"));
        }
    }

    [Fact]
    public void AServerWithoutASchemeIsHttpAndItsPortCounts()
    {
        var h = Client("127.0.0.1:8000");
        Assert.Contains($"x-hydra-access-token: {Token}", Send(h, "http://127.0.0.1:8000/access"));
        Assert.Equal(["User-Agent: MultiVersus/++UE5"], Send(h, "http://127.0.0.1:8001/access"));
    }

    [Fact]
    public void ATokenTheGameAlreadySentIsLeftAloneInAnyCase()
    {
        var h = Client();
        Assert.Equal(["X-Hydra-Access-Token: the-games-own", $"X-OVS-Identity: {Token}"], Send(h, Prod + "/access", "X-Hydra-Access-Token: the-games-own"));
        Assert.Equal(["x-hydra-access-token;", $"X-OVS-Identity: {Token}"], Send(h, Prod + "/access", "x-hydra-access-token;"));
        // A longer name that starts the same is another header.
        Assert.Contains($"x-hydra-access-token: {Token}", Send(h, Prod + "/access", "x-hydra-access-token-old: 1"));
    }

    [Fact]
    public void NoTokenYetMeansNothingAndNoWarning()
    {
        var h = Client();
        _token = "";
        Assert.Equal(["User-Agent: MultiVersus/++UE5"], Send(h, Prod + "/access"));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void AValueThatIsNotPrintableAsciiIsNeverSentAndLoggedOnce()
    {
        var h = Client();
        foreach (string bad in new[] { "abc\r\nX-Evil: 1", "tab\there", "café", "nul\0" })
        {
            _token = bad;
            Assert.Equal(["User-Agent: MultiVersus/++UE5"], Send(h, Prod + "/access"));
        }

        Assert.Equal(2, _log.Lines.Count(l => l.Contains("not printable ASCII")));
        Assert.All(_log.Lines, l => Assert.DoesNotContain("abc", l));
    }

    [Fact]
    public void NamesMustBeTokensAndPathsAbsolute()
    {
        var h = new RequestHeaders([Game], null);
        foreach (string name in new[] { "", "X OVS", "X-OVS:", "X-OVS\r\nEvil", "X-É" })
        {
            Assert.Throws<ArgumentException>(() => h.Add(new HeaderRule(name, () => "v")));
        }

        Assert.Throws<ArgumentException>(() => h.Add(new HeaderRule("X-A", () => "v", Path: "access")));
        h.Add(new HeaderRule("X-Ok_1.2~!", () => "v"));
    }

    [Fact]
    public void TheNoteIsForOneHandleOnOneThreadAndUsedOnce()
    {
        var h = Client();
        nint list = GameList("User-Agent: x");

        h.NoteUrl(7, Prod + "/access");
        Assert.Equal(0, h.AppendNoted(8, list, Append));
        // The mismatch cleared the note too.
        Assert.Equal(0, h.AppendNoted(7, list, Append));

        h.NoteUrl(7, Prod + "/access");
        Assert.Equal(2, h.AppendNoted(7, list, Append));
        Assert.Equal(0, h.AppendNoted(7, list, Append));

        h.NoteUrl(7, Prod + "/access");
        int elsewhere = -1;
        var t = new Thread(() => elsewhere = h.AppendNoted(7, list, Append));
        t.Start();
        t.Join();
        Assert.Equal(0, elsewhere);

        // Another instance's note is not this one's.
        Client().NoteUrl(7, Prod + "/access");
        Assert.Equal(0, h.AppendNoted(7, list, Append));
        Assert.Equal(3, Read(list).Count);
    }

    [Fact]
    public void HeadersAreCopiesInCurlsNodesAndTheHeadIsKept()
    {
        var h = Client();
        nint list = GameList("User-Agent: x", "Content-Type: application/json");
        nint secondNode = *(nint*)(list + 8);
        h.NoteUrl(1, Prod + "/access");
        Assert.Equal(2, h.AppendNoted(1, list, Append));
        Assert.Equal(2, s_appends);
        Assert.Equal(secondNode, *(nint*)(list + 8));

        // The text our side built is gone by now; the list still reads right because curl copied it.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(["User-Agent: x", "Content-Type: application/json", $"x-hydra-access-token: {Token}", $"X-OVS-Identity: {Token}"], Read(list));
    }

    [Fact]
    public void ANullListOrAFailedAppendAddsNothing()
    {
        var h = Client();
        Assert.Equal(0, h.Append(0, ~0UL, Append));
        Assert.Equal(0, s_appends);

        nint list = GameList("User-Agent: x");
        Assert.Equal(0, h.Append(list, ~0UL, &FailingAppend));
        Assert.Equal(["User-Agent: x"], Read(list));
        Assert.Equal(2, _log.Lines.Count(l => l.Contains("could not add")));
    }

    [Fact]
    public void OnlyTheFirstFewSendsAreLoggedAndNeverTheValue()
    {
        var h = Client();
        for (int i = 0; i < 5; i++)
        {
            Send(h, Prod + "/access");
        }

        Assert.Equal(3, _log.Lines.Count(l => l.Contains($"Sent {RequestHeaders.HydraAccessToken} ({Token.Length} characters)")));
        Assert.Equal(3, _log.Lines.Count(l => l.Contains($"Sent {RequestHeaders.OvsIdentity} ({Token.Length} characters)")));
        Assert.All(_log.Lines, l => Assert.DoesNotContain(Token, l));
    }

    [Fact]
    public void ARuleAddedLaterAppliesFromTheNextRequest()
    {
        var h = Client();
        h.NoteUrl(3, Game + "x");
        h.Add(new HeaderRule("X-Later", () => "1"));
        nint list = GameList("User-Agent: x");
        Assert.Equal(1, h.AppendNoted(3, list, Append));
        Assert.Contains("X-Later: 1", Send(h, Game + "x"));
    }
}
