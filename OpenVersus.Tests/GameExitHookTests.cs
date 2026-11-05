using OpenVersus.Hooks;
using OpenVersus.Net;

namespace OpenVersus.Tests;

public class GameExitHookTests
{
    [SkippableFact]
    public void ThePatternFindsRequestExitOnceInSteamsBuild()
    {
        byte[] mapped = FinalBuild.Mapped();
        int at = FinalBuild.Single(mapped, GameExitHook.Pattern);
        Assert.Equal(0x02BCD0A0, at);
        // sub rsp, 0x28; test cl, cl; je +0x17: the function's own start, not a site inside another.
        Assert.Equal(new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x84, 0xC9, 0x74, 0x17 }, mapped.AsSpan(at, 8).ToArray());
        // The non-forced branch ends in PostQuitMessage: an indirect jmp through the import table, after the reason's lea and two calls.
        Assert.Equal(new byte[] { 0x48, 0xFF, 0x25 }, mapped.AsSpan(at + 0x3D, 3).ToArray());
    }

    [Fact]
    public void WithoutTheFunctionARequestSaysSoAndReturnsFalse()
    {
        var log = new ListLogger();
        Assert.False(GameExitHook.Request(log));
        Assert.Contains(log.Lines, l => l.Contains("not resolved"));
    }

    [Fact]
    public void AQuitIsReportedAsOnePostAndTheAnswerIsOnlyLogged()
    {
        var http = new Posts(new HttpResult(true, 200, "{\"counted\":true,\"count\":1}"u8.ToArray(), null));
        var log = new ListLogger();

        Assert.True(GameExitHook.Report(http, "http://ovs.test/ovs/quit", forced: false, log));

        var (url, type, body, timeout) = Assert.Single(http.Sent);
        Assert.Equal(("http://ovs.test/ovs/quit", "application/json", "{\"forced\":false}", GameExitHook.ReportTimeout), (url, type, body, timeout));
        Assert.Contains(log.Lines, l => l.Contains("reported to the server") && l.Contains("counted"));

        Assert.False(GameExitHook.Report(new Posts(), "http://ovs.test/ovs/quit", forced: true, log));
        Assert.Contains(log.Lines, l => l.Contains("(forced)") && l.Contains("did not land"));
    }

    private sealed class Posts(params HttpResult[] results) : IHttpTransport
    {
        private int _next;

        public List<(string Url, string Type, string Body, TimeSpan Timeout)> Sent { get; } = [];

        public HttpResult Get(Uri url, TimeSpan timeout) => throw new NotSupportedException();

        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout)
        {
            Sent.Add((url.ToString(), contentType, System.Text.Encoding.UTF8.GetString(body), timeout));
            return _next < results.Length ? results[_next++] : HttpResult.Failed("no response");
        }
    }

    [Fact]
    public void TheStatusLineNamesIt()
    {
        Assert.Contains("gameExit=True", new HookStatus { GameExit = true }.ToString());
    }
}
