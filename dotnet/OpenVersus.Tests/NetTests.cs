using OpenVersus.Identity;
using OpenVersus.Net;

namespace OpenVersus.Tests;

public class NetTests
{
    [Fact]
    public void UrlsJoinStripsTheTrailingSlashAndKeepsTheScheme()
    {
        Assert.Equal("https://prod.openversus.org/api/identify", Urls.Join("https://prod.openversus.org/", "/api/identify")!.ToString());
        Assert.Equal("http://testing.openversus.org:8000/ovs/notifications", Urls.Join("http://testing.openversus.org:8000/", "/ovs/notifications")!.ToString());
        Assert.Equal("http://host/x", Urls.Join("host", "/x")!.ToString());
        Assert.Null(Urls.Join("", "/x"));
    }

    [Theory]
    [InlineData("http://blah.something.com/", "http", 80, "http://blah.something.com/ovs/notifications")]
    [InlineData("http://blah.something.com:80/", "http", 80, "http://blah.something.com/ovs/notifications")]
    [InlineData("https://blah.something.com/", "https", 443, "https://blah.something.com/ovs/notifications")]
    [InlineData("https://blah.something.com:443/", "https", 443, "https://blah.something.com/ovs/notifications")]
    [InlineData("https://blah.something.com:57013/", "https", 57013, "https://blah.something.com:57013/ovs/notifications")]
    [InlineData("blah.something.com:57013", "http", 57013, "http://blah.something.com:57013/ovs/notifications")]
    public void ServerUrlsWithAndWithoutPortsParse(string text, string scheme, int port, string joined)
    {
        var url = Urls.Parse(text);
        Assert.NotNull(url);
        Assert.Equal(scheme, url!.Scheme);
        Assert.Equal(port, url.Port);
        Assert.Equal("blah.something.com", url.Host);
        Assert.Equal(joined, Urls.Join(text, "/ovs/notifications")!.ToString());
    }

    [Fact]
    public void VersionResponseParses()
    {
        var info = AutoUpdate.Parse("""{"latest_version":"2026.04.08.14","download_url":"https://example.org/OpenVersus.asi","is_latest":true,"release_name":"2026.04.08.14 prerelease"}""");
        Assert.NotNull(info);
        Assert.True(info!.IsLatest);
        Assert.Equal("2026.04.08.14", info.LatestVersion);
        Assert.Equal("https://example.org/OpenVersus.asi", info.DownloadUrl);
        Assert.Null(AutoUpdate.Parse("not json"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("2", false)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\"tRuE\"", true)]
    [InlineData("\"on\"", true)]
    [InlineData("\"OFF\"", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("[1]", false)]
    public void IsLatestForgivesTheServersSpelling(string json, bool expected)
    {
        var info = AutoUpdate.Parse($$"""{"latest_version":"1","is_latest":{{json}}}""");
        Assert.NotNull(info);
        Assert.Equal(expected, info!.IsLatest);
    }

    [Fact]
    public void NotificationsParseAndUnknownFieldsAreTolerated()
    {
        var list = NotificationPoller.Parse("""[{"type":"admin_banner","title":"Top","message":"Bottom","timeout":10.0,"timestamp":1},{"type":"match_cancel"},{"nope":1},{"type":"toast_received","message":"X toasted you!","extra":[1,2]}]""");
        Assert.Equal(3, list.Count);
        Assert.Equal(("admin_banner", "Top", "Bottom", 10.0), (list[0].Type, list[0].Title, list[0].Message, list[0].Timeout));
        Assert.Equal(("match_cancel", "", "", (double?)null), (list[1].Type, list[1].Title, list[1].Message, list[1].Timeout));
        Assert.Equal("X toasted you!", list[2].Message);
        Assert.Empty(NotificationPoller.Parse("{}"));
        Assert.Empty(NotificationPoller.Parse("garbage"));
    }

    [Fact]
    public void VersionStringsForgiveTheServersTypes()
    {
        var info = AutoUpdate.Parse("""{"latest_version":20260924,"download_url":null,"is_latest":false,"release_name":{"x":1}}""");
        Assert.NotNull(info);
        Assert.Equal("20260924", info!.LatestVersion);
        Assert.Null(info.DownloadUrl);
        Assert.Null(info.ReleaseName);
    }

    [Fact]
    public void OneBadNotificationDoesNotLoseTheOthers()
    {
        var list = NotificationPoller.Parse("""[{"type":"toast_received","title":5},{"type":"admin_banner","title":"ok","message":"m"},{"type":"toast_received","timeout":"soon"}]""", out var problems);
        Assert.Single(list);
        Assert.Equal("ok", list[0].Title);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Contains("skipping", p));
        Assert.Empty(NotificationPoller.Parse("garbage", out problems));
        Assert.Contains(problems, p => p.Contains("not JSON"));
    }

    [Theory]
    [InlineData("2026.09.25.01", "2026.09.24.01", true)]
    [InlineData("2026.09.24.02", "2026.09.24.01", true)]
    [InlineData("2026.09.24.01", "2026.09.24.01", false)]
    [InlineData("2026.09.23.09", "2026.09.24.01", false)]
    [InlineData(" 2026.10.01.01 ", "2026.09.24.01", true)]
    public void OnlyALaterVersionIsAnUpdate(string offered, string running, bool expected) => Assert.Equal(expected, AutoUpdate.IsNewer(offered, running));

    [Fact]
    public void AnUpdateInstallsUnderTheNewVersionsName()
    {
        string plugins = Path.Combine(Path.GetTempPath(), "plugins");
        Assert.Equal(Path.Combine(plugins, "OpenVersus_2026.10.01.01.asi"), AutoUpdate.InstallPath(Path.Combine(plugins, "OpenVersus_2026.09.24.02.asi"), " 2026.10.01.01 "));
        // A plain OpenVersus.asi from an earlier release moves to the versioned name too.
        Assert.Equal(Path.Combine(plugins, "OpenVersus_2026.10.01.01.asi"), AutoUpdate.InstallPath(Path.Combine(plugins, "OpenVersus.asi"), "2026.10.01.01"));
    }

    [Fact]
    public void DownloadsThatAreNotAPluginAreRefused()
    {
        Assert.Contains("too small", AutoUpdate.Validate(new byte[9999]));
        Assert.Contains("not a Windows binary", AutoUpdate.Validate(new byte[20000]));
        var html = new byte[20000];
        "<!DOCTYPE html><html>"u8.CopyTo(html);
        Assert.Contains("not a Windows binary", AutoUpdate.Validate(html));

        // "MZ" with a header offset past the end of the body must be refused, not thrown.
        var junk = new byte[20000];
        junk[0] = (byte)'M';
        junk[1] = (byte)'Z';
        BitConverter.TryWriteBytes(junk.AsSpan(0x3C), 0x7FFFFFFF);
        Assert.Contains("outside the file", AutoUpdate.Validate(junk));

        // The smallest thing that passes: an MZ header pointing at a PE signature with a size.
        var pe = new byte[20000];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BitConverter.TryWriteBytes(pe.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(pe.AsSpan(0x80));
        BitConverter.TryWriteBytes(pe.AsSpan(0x80 + 24 + 56), 0x1000u);
        Assert.Null(AutoUpdate.Validate(pe));
    }

    [Fact]
    public void FingerprintTextIsTheCppFormat()
    {
        // "%d|%d|%d|%d|%08X|%ls": signed decimal registers, upper-case zero-padded hex, serial as is.
        Assert.Equal("13|1970169159|1818588270|1231384169|000A0655|ABC123", EnvInfo.FingerprintText((13, 1970169159, 1818588270, 1231384169), (0x000A0655, 0, 0, 0), "ABC123"));
        Assert.Equal("-1|0|0|0|FFFFFFFF|Unknown", EnvInfo.FingerprintText((-1, 0, 0, 0), (-1, 0, 0, 0), "Unknown"));
    }

    [Fact]
    public void HardwareIdIsLowercaseSha256OfTheFingerprint()
    {
        string id = EnvInfo.ComputeHardwareId((13, 1970169159, 1818588270, 1231384169), (0x000A0655, 0, 0, 0), "ABC123");
        Assert.Equal(64, id.Length);
        Assert.Equal(id, id.ToLowerInvariant());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("13|1970169159|1818588270|1231384169|000A0655|ABC123"))), id);
    }

    [Fact]
    public void IdentityBodyIsValidJsonWithEscaping()
    {
        var env = new EnvInfo { SteamId = "7656119\"quoted\\" };
        string body = IdentityRegistration.Body(env);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("7656119\"quoted\\", doc.RootElement.GetProperty("steamId").GetString());
        Assert.Equal(OvsVersion.Current, doc.RootElement.GetProperty("clientVersion").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("hardwareId").GetString()!.Length);
    }

    /// <summary>The smallest body <see cref="AutoUpdate.Validate"/> accepts, marked so it can be told apart.</summary>
    private static byte[] Plugin(byte mark)
    {
        var pe = new byte[20000];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BitConverter.TryWriteBytes(pe.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(pe.AsSpan(0x80));
        BitConverter.TryWriteBytes(pe.AsSpan(0x80 + 24 + 56), 0x1000u);
        pe[^1] = mark;
        return pe;
    }

    private static byte[] Zip(params (string Name, byte[] Body)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(body);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void ThePluginInAReleaseZipIsInstalledAndNothingElse()
    {
        byte[] release = Zip(
            ("xinput1_3.dll", Plugin(1)),
            ("plugins/OpenVersus_2026.10.01.01.asi", Plugin(2)),
            ("plugins/OpenVersus.toml", "[Settings]\n"u8.ToArray()));

        byte[]? plugin = AutoUpdate.PluginFrom(release, "2026.10.01.01", out string source, out string? problem);
        Assert.Null(problem);
        Assert.Equal(Plugin(2), plugin);
        Assert.Equal("plugins/OpenVersus_2026.10.01.01.asi from the zip", source);
    }

    [Fact]
    public void ABareAsiIsInstalledAsBefore()
    {
        Assert.Equal(Plugin(3), AutoUpdate.PluginFrom(Plugin(3), "2026.10.01.01", out string source, out _));
        Assert.Equal("the download", source);
    }

    [Fact]
    public void AZipIsRefusedUnlessOnePluginInItIsClearlyTheOne()
    {
        // Several .asi files: the one named for the offered version wins.
        byte[] two = Zip(("OpenVersus_2026.09.01.01.asi", Plugin(4)), ("OpenVersus_2026.10.01.01.asi", Plugin(5)));
        Assert.Equal(Plugin(5), AutoUpdate.PluginFrom(two, "2026.10.01.01", out _, out _));
        Assert.Null(AutoUpdate.PluginFrom(two, "2026.11.01.01", out _, out string? ambiguous));
        Assert.Contains("2 .asi files and none named OpenVersus_2026.11.01.01.asi", ambiguous);

        Assert.Null(AutoUpdate.PluginFrom(Zip(("readme.txt", "hi"u8.ToArray())), "2026.10.01.01", out _, out string? none));
        Assert.Equal("a zip with no .asi in it", none);

        Assert.Null(AutoUpdate.PluginFrom(Zip(("OpenVersus.asi", new byte[20000])), "2026.10.01.01", out _, out string? notPe));
        Assert.Contains("OpenVersus.asi is not a Windows binary", notPe);

        byte[] damaged = Zip(("OpenVersus.asi", Plugin(6)))[..40];
        Assert.Null(AutoUpdate.PluginFrom(damaged, "2026.10.01.01", out _, out string? broken));
        Assert.StartsWith("a damaged zip", broken);
    }

    /// <summary>Serves a fixed result per URL, 404 for anything else, and records what was asked for.</summary>
    private sealed class FakeHttp(Dictionary<string, HttpResult> results) : IHttpTransport
    {
        public List<string> Requested { get; } = [];

        public HttpResult Get(Uri url, TimeSpan timeout)
        {
            Requested.Add(url.ToString());
            return results.TryGetValue(url.ToString(), out var result) ? result : new HttpResult(false, 404, [], null);
        }

        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout) => throw new NotSupportedException();
    }

    private const string AsiUrl = "https://github.com/openversus/ovs-client/releases/download/2026.10.01.01/OpenVersus_2026.10.01.01.asi";

    private static string Sha256Line(byte[] body, string name) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant() + " *" + name + "\n";

    private static HttpResult Served(byte[] body) => new(true, 200, body, null);

    private static (byte[]? Plugin, ListLogger Log, FakeHttp Http) FetchWith(HttpResult? checksum, byte[]? body = null, string url = AsiUrl)
    {
        body ??= Plugin(7);
        var results = new Dictionary<string, HttpResult> { [url] = Served(body) };
        if (checksum != null)
        {
            results[url + ".sha256"] = checksum;
        }

        var http = new FakeHttp(results);
        var log = new ListLogger();
        var update = new AutoUpdate("https://prod.openversus.org/", "OpenVersus_2026.09.25.06.asi", http, http, log, () => { });
        byte[]? plugin = update.Fetch(new VersionInfo("2026.10.01.01", url, false, ""));
        return (plugin, log, http);
    }

    [Fact]
    public void TheChecksumFileIsReadAsSha256sumWritesIt()
    {
        byte[] body = Plugin(8);
        string line = Sha256Line(body, "OpenVersus_2026.10.01.01.asi");
        Assert.Null(AutoUpdate.CheckSha256(line, "OpenVersus_2026.10.01.01.asi", body));
        Assert.Null(AutoUpdate.CheckSha256(line.ToUpperInvariant().Replace(" *OPENVERSUS_2026.10.01.01.ASI", " *OpenVersus_2026.10.01.01.asi"), "OpenVersus_2026.10.01.01.asi", body));
        Assert.Null(AutoUpdate.CheckSha256(line.Split(' ')[0], "OpenVersus_2026.10.01.01.asi", body));

        Assert.Contains("but the release says", AutoUpdate.CheckSha256(line, "OpenVersus_2026.10.01.01.asi", Plugin(9)));
        Assert.Contains("is for OpenVersus_2026.10.01.01.asi, not other.asi", AutoUpdate.CheckSha256(line, "other.asi", body));
        Assert.Contains("does not hold a SHA-256", AutoUpdate.CheckSha256("", "x.asi", body));
        Assert.Contains("does not hold a SHA-256", AutoUpdate.CheckSha256("<html>Not Found</html>", "x.asi", body));
    }

    [Fact]
    public void AMatchingChecksumInstalls()
    {
        byte[] body = Plugin(10);
        var (plugin, log, http) = FetchWith(Served(System.Text.Encoding.UTF8.GetBytes(Sha256Line(body, "OpenVersus_2026.10.01.01.asi"))), body);
        Assert.Equal(body, plugin);
        Assert.Equal([AsiUrl, AsiUrl + ".sha256"], http.Requested);
        Assert.Contains(log.Lines, l => l.Contains("SHA-256 matches"));
    }

    [Fact]
    public void AMismatchedChecksumDoesNotInstall()
    {
        var (plugin, log, _) = FetchWith(Served(System.Text.Encoding.UTF8.GetBytes(Sha256Line(Plugin(11), "OpenVersus_2026.10.01.01.asi"))), Plugin(12));
        Assert.Null(plugin);
        Assert.Contains(log.Lines, l => l.Contains("Not installing the download") && l.Contains("but the release says"));
    }

    /// <summary>No .sha256 is treated like a mismatch: nothing this client could install lacks one.</summary>
    [Fact]
    public void AReleaseWithoutAChecksumIsNotInstalled()
    {
        var (plugin, log, _) = FetchWith(checksum: null);
        Assert.Null(plugin);
        Assert.Contains(log.Lines, l => l.Contains("Not installing the download: the release publishes no checksum"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(0)]
    public void AChecksumThatCannotBeFetchedWaitsForTheNextLaunch(int status)
    {
        var failed = status == 0 ? HttpResult.Failed("timed out") : new HttpResult(false, status, [], null);
        var (plugin, log, _) = FetchWith(failed);
        Assert.Null(plugin);
        Assert.Contains(log.Lines, l => l.Contains("Could not fetch the checksum") && l.Contains("trying again next launch"));
    }

    /// <summary>A zip is checked as downloaded, against the zip's own .sha256, before anything is taken out of it.</summary>
    [Fact]
    public void AZipIsCheckedAgainstTheZipsChecksum()
    {
        const string zipUrl = "https://github.com/openversus/ovs-client/releases/download/2026.10.01.01/OpenVersus_v2026.10.01.01.zip";
        byte[] zip = Zip(("plugins/OpenVersus_2026.10.01.01.asi", Plugin(13)));
        var (plugin, _, _) = FetchWith(Served(System.Text.Encoding.UTF8.GetBytes(Sha256Line(zip, "OpenVersus_v2026.10.01.01.zip"))), zip, zipUrl);
        Assert.Equal(Plugin(13), plugin);

        var (refused, _, _) = FetchWith(Served(System.Text.Encoding.UTF8.GetBytes(Sha256Line(Plugin(13), "OpenVersus_v2026.10.01.01.zip"))), zip, zipUrl);
        Assert.Null(refused);
    }
}
