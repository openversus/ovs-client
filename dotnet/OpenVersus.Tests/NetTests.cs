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
}
