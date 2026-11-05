using OpenVersus.Net;

namespace OpenVersus.Tests;

public class PluginInstallTests : IDisposable
{
    private const string Running = "OpenVersus_2026.09.25.06.asi";
    private const string Offered = "2026.10.01.01";
    private readonly string _home = Directory.CreateTempSubdirectory("ovs-install-").FullName;
    private readonly ListLogger _log = new();

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string In(string name) => Path.Combine(_home, name);

    private AutoUpdate Updater(string plugin)
    {
        File.WriteAllText(In(plugin), "running");
        var http = new NoServer();
        return new AutoUpdate("http://ovs.test", In(plugin), _home, http, http, _log, () => { });
    }

    [Fact]
    public void TheNewPluginGoesInAndTheRunningOneBecomesBak()
    {
        var update = Updater(Running);

        Assert.True(update.InstallPlugin("new"u8.ToArray(), Offered));
        Assert.Equal("new", File.ReadAllText(AutoUpdate.InstallPath(_home, Offered)));
        Assert.Equal("running", File.ReadAllText(In(Running + ".bak")));
        Assert.False(File.Exists(In(Running)));
        Assert.Equal([Running + ".bak", $"OpenVersus_{Offered}.asi"], Directory.GetFiles(_home).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void ARunningPluginThatCannotBeRenamedLeavesTheInstallAsItWas()
    {
        var update = Updater(Running);
        Directory.CreateDirectory(In(Running + ".bak"));

        Assert.False(update.InstallPlugin("new"u8.ToArray(), Offered));
        Assert.Equal("running", File.ReadAllText(In(Running)));
        Assert.Equal([Running], Directory.GetFiles(_home).Select(Path.GetFileName));
    }

    [Fact]
    public void ANewPluginThatCannotGoInLeavesTheInstallAsItWas()
    {
        var update = Updater(Running);
        Directory.CreateDirectory(AutoUpdate.InstallPath(_home, Offered));

        Assert.False(update.InstallPlugin("new"u8.ToArray(), Offered));
        Assert.Equal("running", File.ReadAllText(In(Running)));
        Assert.Equal([Running], Directory.GetFiles(_home).Select(Path.GetFileName));
    }

    [Fact]
    public void APluginWithTheRunningOnesNameReplacesIt()
    {
        string same = $"OpenVersus_{Offered}.asi";
        var update = Updater(same);

        Assert.True(update.InstallPlugin("new"u8.ToArray(), Offered));
        Assert.Equal("new", File.ReadAllText(In(same)));
        Assert.Equal("running", File.ReadAllText(In(same + ".bak")));
        Assert.Equal([same, same + ".bak"], Directory.GetFiles(_home).Select(Path.GetFileName).Order());
    }

    [Theory]
    [InlineData(true, "2026.10.01.01", "https://x/y.asi", "Already up to date")]
    [InlineData(false, "", "https://x/y.asi", "Missing version/URL")]
    [InlineData(false, "2026.10.01.01", null, "Missing version/URL")]
    [InlineData(false, "2026.09.01.01", "https://x/y.asi", "not installing")]
    [InlineData(false, "2026.10.01.01", "https://x/y.asi", null)]
    public void OnlyANewerCompleteOfferIsAnUpdate(bool isLatest, string latest, string? url, string? reason)
    {
        string? notOffered = AutoUpdate.NotOffered(new VersionInfo(latest, url, isLatest, ""), "2026.09.25.06");
        if (reason == null)
        {
            Assert.Null(notOffered);
        }
        else
        {
            Assert.Contains(reason, notOffered);
        }
    }

    /// <summary>Installing never goes to the network.</summary>
    private sealed class NoServer : IHttpTransport
    {
        public HttpResult Get(Uri url, TimeSpan timeout) => throw new NotSupportedException();
        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout) => throw new NotSupportedException();
    }
}
