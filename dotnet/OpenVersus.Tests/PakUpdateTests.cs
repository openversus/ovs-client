using System.Security.Cryptography;
using System.Text;
using OpenVersus.Net;

namespace OpenVersus.Tests;

public class PakUpdateTests : IDisposable
{
    private const string Release = "https://github.com/openversus/ovs-paks/releases/download/content/";
    private readonly string _root = Directory.CreateTempSubdirectory("ovs-paks-").FullName;
    private readonly string _paks, _plugin, _content;
    private readonly ListLogger _log = new();

    public PakUpdateTests()
    {
        // The layout the client uses: paks in AppData's Saved\Paks, the updater's files beside it,
        // and the game's own Content\Paks where they were installed by hand before.
        _paks = Directory.CreateDirectory(Path.Combine(_root, "AppData", "MultiVersus", "Saved", "Paks")).FullName;
        _plugin = Directory.CreateDirectory(Path.Combine(_root, "AppData", "MultiVersus", "Saved", "OpenVersus")).FullName;
        _content = Directory.CreateDirectory(Path.Combine(_root, "Game", "MultiVersus", "Content", "Paks")).FullName;
    }

    private string OldContentBackup => Path.Combine(_plugin, "old-content-paks");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Staging => Path.Combine(_plugin, "update-staging");
    private string Backup => Path.Combine(_plugin, "pak-backup");

    private PakUpdate Updater(ServedFiles http, string owner = PakUpdate.DefaultOwner) =>
        new(_paks, Staging, Backup, Path.Combine(_plugin, "PakHashes.txt"), http, _log, owner)
        {
            Sleep = _ => { },
            LegacyDirectory = _content,
            LegacyBackupDirectory = OldContentBackup,
        };

    private static string Sha(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

    private static UpdateFile Entry(string name, byte[] body, string? url = null) =>
        new(name, "paks", body.Length, Sha(body), url ?? Release + name);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // Manifest checks

    [Theory]
    [InlineData("OVS_P.pak", true)]
    [InlineData("ovs_skins-2.utoc", true)]
    [InlineData("OVS_P.ucas", true)]
    [InlineData("OVS_P.sig", true)]
    [InlineData("OVS_P.asi", false)]
    [InlineData("pakchunk0-WindowsNoEditor.pak", false)]
    [InlineData("OVS_..\\..\\x.pak", false)]
    [InlineData("OVS_a/b.pak", false)]
    [InlineData("OVS_P.pak:stream", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyOvsContentFilesArePaks(string? name, bool ok) => Assert.Equal(ok, PakUpdate.IsPakName(name));

    [Theory]
    [InlineData("https://github.com/openversus/ovs-client/releases/download/2026.10.01.1/OVS_P.pak", true)]
    [InlineData("https://github.com/openversus/ovs-paks/releases/download/content/OVS_P.pak", true)]
    [InlineData("https://GitHub.com/openversus/ovs-paks/releases/download/content/OVS_P.pak", true)]
    [InlineData("http://github.com/openversus/ovs-paks/releases/download/content/OVS_P.pak", false)]
    [InlineData("https://github.com/someone/ovs-paks/releases/download/content/OVS_P.pak", false)]
    [InlineData("https://github.com/openversus/ovs-paks/releases/download/content/OVS_Q.pak", false)]
    [InlineData("https://github.com/openversus/ovs-paks/raw/main/OVS_P.pak", false)]
    [InlineData("https://github.com/openversus/ovs-paks/releases/download/a/b/OVS_P.pak", false)]
    [InlineData("https://github.com:8443/openversus/ovs-paks/releases/download/content/OVS_P.pak", false)]
    [InlineData("https://evil@github.com/openversus/ovs-paks/releases/download/content/OVS_P.pak", false)]
    [InlineData("https://objects.githubusercontent.com/openversus/ovs-paks/releases/download/content/OVS_P.pak", false)]
    [InlineData(null, false)]
    public void PaksDownloadOnlyFromOpenversusReleases(string? url, bool ok) => Assert.Equal(ok, PakUpdate.IsAllowedUrl(url, "OVS_P.pak"));

    [Fact]
    public void AForkIsAllowedOnlyWhenItIsTheConfiguredOwner()
    {
        const string Fork = "https://github.com/tuggernuts1123/ovs-client/releases/download/2026.09.27.1/OVS_P.pak";
        Assert.False(PakUpdate.IsAllowedUrl(Fork, "OVS_P.pak"));
        Assert.True(PakUpdate.IsAllowedUrl(Fork, "OVS_P.pak", "tuggernuts1123"));
        Assert.False(PakUpdate.IsAllowedUrl("https://github.com/openversus/ovs-paks/releases/download/content/OVS_P.pak", "OVS_P.pak", "tuggernuts1123"));

        Assert.Equal("tuggernuts1123", Updater(new ServedFiles(), "tuggernuts1123").ReleaseOwner);
        Assert.Equal(PakUpdate.DefaultOwner, Updater(new ServedFiles(), "../evil").ReleaseOwner);
        Assert.Equal(PakUpdate.DefaultOwner, Updater(new ServedFiles(), "").ReleaseOwner);
    }

    [Fact]
    public void AFlakyDownloadIsRetried()
    {
        byte[] pak = Bytes("pak body");
        var http = new ServedFiles { [Release + "OVS_P.pak"] = pak };
        http.FailFirst = 2;
        long bytes = 0;

        Assert.True(Updater(http).Download([Entry("OVS_P.pak", pak)], onBytes: b => bytes += b));
        Assert.Equal(3, http.Requested.Count);
        Assert.Equal(pak.Length, bytes);
    }

    [Fact]
    public void AReleaseWithoutPaksHasNothingToDo()
    {
        var plugin = new UpdateFile("OpenVersus_2026.10.01.1.asi", "plugin", 5, new string('a', 64), "https://github.com/openversus/ovs-client/releases/download/2026.10.01.1/OpenVersus_2026.10.01.1.asi");
        Assert.Empty(PakUpdate.Paks(new ReleaseFiles([plugin]), out var problem)!);
        Assert.Null(problem);
        Assert.Empty(PakUpdate.Paks(new ReleaseFiles(null), out _)!);
        Assert.Empty(PakUpdate.Paks(null, out _)!);
    }

    [Fact]
    public void OneBadEntryRefusesTheWholeRelease()
    {
        var good = Entry("OVS_P.pak", Bytes("pak"));
        Assert.Null(PakUpdate.Paks(new ReleaseFiles([good, good with { Sha256 = "abc" }]), out var problem));
        Assert.Contains("SHA-256", problem);
        Assert.Null(PakUpdate.Paks(new ReleaseFiles([good, good]), out problem));
        Assert.Contains("twice", problem);
        Assert.Null(PakUpdate.Paks(new ReleaseFiles([good with { Size = 0 }]), out problem));
        Assert.Null(PakUpdate.Paks(new ReleaseFiles([good with { DownloadUrl = "https://example.com/OVS_P.pak" }]), out problem));
        Assert.Null(PakUpdate.Paks(new ReleaseFiles([good with { Name = "..\\OVS_P.pak" }]), out problem));
    }

    // What is due

    [Fact]
    public void MissingDifferentAndMatchingPaksAreToldApart()
    {
        byte[] same = Bytes("same"), changed = Bytes("new!"), resized = Bytes("longer now"), missing = Bytes("missing");
        File.WriteAllBytes(Path.Combine(_paks, "OVS_Same.pak"), same);
        File.WriteAllBytes(Path.Combine(_paks, "OVS_Changed.pak"), Bytes("old!"));
        File.WriteAllBytes(Path.Combine(_paks, "OVS_Resized.pak"), Bytes("short"));

        var needed = Updater(new ServedFiles()).Needed([Entry("OVS_Same.pak", same), Entry("OVS_Changed.pak", changed), Entry("OVS_Resized.pak", resized), Entry("OVS_Missing.pak", missing)]);

        Assert.Equal(["OVS_Changed.pak", "OVS_Resized.pak", "OVS_Missing.pak"], needed.Select(f => f.Name));
    }

    [Fact]
    public void UnchangedPaksAreNotHashedAgain()
    {
        string path = Path.Combine(_paks, "OVS_P.pak");
        byte[] body = Bytes("original");
        File.WriteAllBytes(path, body);
        var updater = Updater(new ServedFiles());
        Assert.Empty(updater.Needed([Entry("OVS_P.pak", body)]));

        // Same size and modified time: the cached hash stands, so the edit goes unseen.
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, Bytes("0riginal"));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Empty(updater.Needed([Entry("OVS_P.pak", body)]));

        // A new modified time means a new hash.
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));
        Assert.Single(updater.Needed([Entry("OVS_P.pak", body)]));
    }

    // Download and install

    [Fact]
    public void AnUpdateIsDownloadedVerifiedAndInstalledWithBackups()
    {
        byte[] pak = Bytes("new pak"), utoc = Bytes("new utoc");
        File.WriteAllBytes(Path.Combine(_paks, "OVS_P.pak"), Bytes("old pak"));
        var files = new List<UpdateFile> { Entry("OVS_P.pak", pak), Entry("OVS_P.utoc", utoc) };
        var http = new ServedFiles { [Release + "OVS_P.pak"] = pak, [Release + "OVS_P.utoc"] = utoc };
        var updater = Updater(http);
        var shown = new List<string>();
        long bytes = 0;

        Assert.True(updater.Download(files, (name, n, count) => shown.Add($"{name} {n}/{count}"), b => bytes += b));
        Assert.Equal(["OVS_P.pak 1/2", "OVS_P.utoc 2/2"], shown);
        Assert.Equal(pak.Length + utoc.Length, bytes);
        Assert.Equal("old pak", File.ReadAllText(Path.Combine(_paks, "OVS_P.pak")));

        Assert.True(updater.Install(files));
        Assert.Equal(pak, File.ReadAllBytes(Path.Combine(_paks, "OVS_P.pak")));
        Assert.Equal(utoc, File.ReadAllBytes(Path.Combine(_paks, "OVS_P.utoc")));
        Assert.Equal("old pak", File.ReadAllText(Path.Combine(Backup, "OVS_P.pak")));
        Assert.False(File.Exists(Path.Combine(Backup, "OVS_P.utoc")));
        Assert.False(Directory.Exists(Staging));
        Assert.Empty(updater.Needed(files));
        Assert.Equal(2, http.Requested.Count);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("cut")]
    [InlineData("missing")]
    public void AFileThatFailsItsCheckChangesNothing(string how)
    {
        byte[] good = Bytes("good pak"), other = Bytes("other");
        File.WriteAllBytes(Path.Combine(_paks, "OVS_P.pak"), Bytes("old pak"));
        var http = new ServedFiles { [Release + "OVS_A.pak"] = other };
        if (how == "tampered")
        {
            http[Release + "OVS_P.pak"] = Bytes("evil pak");
        }
        else if (how == "cut")
        {
            http[Release + "OVS_P.pak"] = good[..4];
        }

        var updater = Updater(http);
        Assert.False(updater.Download([Entry("OVS_A.pak", other), Entry("OVS_P.pak", good)]));
        Assert.False(Directory.Exists(Staging));
        Assert.Equal("old pak", File.ReadAllText(Path.Combine(_paks, "OVS_P.pak")));
        Assert.False(File.Exists(Path.Combine(_paks, "OVS_A.pak")));
        Assert.Contains(_log.Lines, l => l.Contains("Not installing OVS_P.pak"));
    }

    [Fact]
    public void AFailedInstallPutsEverythingBack()
    {
        byte[] a = Bytes("new a"), b = Bytes("new b");
        File.WriteAllBytes(Path.Combine(_paks, "OVS_A.pak"), Bytes("old a"));
        File.WriteAllBytes(Path.Combine(_paks, "OVS_B.pak"), Bytes("old b"));
        var files = new List<UpdateFile> { Entry("OVS_A.pak", a), Entry("OVS_B.pak", b) };
        var updater = Updater(new ServedFiles { [Release + "OVS_A.pak"] = a, [Release + "OVS_B.pak"] = b });
        Assert.True(updater.Download(files));
        File.Delete(Path.Combine(Staging, "OVS_B.pak")); // the second move fails

        Assert.False(updater.Install(files));
        Assert.Equal("old a", File.ReadAllText(Path.Combine(_paks, "OVS_A.pak")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(_paks, "OVS_B.pak")));
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void ANewPakThatFailsToInstallIsRemovedAgain()
    {
        byte[] a = Bytes("brand new"), b = Bytes("new b");
        var files = new List<UpdateFile> { Entry("OVS_New.pak", a), Entry("OVS_B.pak", b) };
        var updater = Updater(new ServedFiles { [Release + "OVS_New.pak"] = a, [Release + "OVS_B.pak"] = b });
        Assert.True(updater.Download(files));
        File.Delete(Path.Combine(Staging, "OVS_B.pak"));

        Assert.False(updater.Install(files));
        Assert.False(File.Exists(Path.Combine(_paks, "OVS_New.pak")));
    }

    // The startup step

    private StartupUpdate Startup(ServedFiles http, List<string> told, List<string> notices, bool autoUpdate = false, bool withPaks = true) =>
        new("http://ovs.test", autoUpdate, http,
            new AutoUpdate("http://ovs.test", Path.Combine(_plugin, "OpenVersus.asi"), _plugin, http, http, _log, () => { }),
            withPaks ? Updater(http) : null, _log, () => { }, notices.Add)
        {
            Exit = () => told.Add("exit"),
            Tell = (text, caption, _) => told.Add(caption),
            OpenWindow = _ => null,
        };

    private static string VersionUrl => $"http://ovs.test/ovs/client-version?v={Uri.EscapeDataString(OvsVersion.Current)}";

    private static byte[] VersionJson(params UpdateFile[] paks)
    {
        string files = string.Join(",", paks.Select(p => $"{{\"name\":\"{p.Name}\",\"kind\":\"paks\",\"size\":{p.Size},\"sha256\":\"{p.Sha256}\",\"download_url\":\"{p.DownloadUrl}\"}}"));
        return Bytes($"{{\"latest_version\":\"{OvsVersion.Current}\",\"download_url\":\"\",\"is_latest\":true,\"release_name\":\"x\",\"files\":[{files}],\"file_count\":{paks.Length}}}");
    }

    [Fact]
    public void AReleaseWithoutPaksLeavesTheLaunchAlone()
    {
        var told = new List<string>();
        var http = new ServedFiles { [VersionUrl] = VersionJson() };

        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, told, []).Run());
        Assert.Empty(told);
        Assert.Equal([VersionUrl], http.Requested);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("Not using this release's paks"));
    }

    [Fact]
    public void AResponseWithoutAFileListIsAReleaseWithoutPaks()
    {
        var http = new ServedFiles { [VersionUrl] = Bytes($"{{\"latest_version\":\"{OvsVersion.Current}\",\"is_latest\":true}}") };

        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, [], []).Run());
        Assert.DoesNotContain(_log.Lines, l => l.Contains("Not using this release's paks"));
    }

    [Fact]
    public void ASizeSentAsAStringIsRead()
    {
        byte[] pak = Bytes("endgame content");
        var entry = Entry("OVS_P.pak", pak);
        string json = Encoding.UTF8.GetString(VersionJson(entry)).Replace($"\"size\":{pak.Length}", $"\"size\":\"{pak.Length}\"");
        var http = new ServedFiles { [VersionUrl] = Bytes(json), [entry.DownloadUrl!] = pak };

        Assert.Equal(StartupUpdate.Outcome.Installed, Startup(http, [], []).Run());
        Assert.Equal(pak, File.ReadAllBytes(Path.Combine(_paks, "OVS_P.pak")));
    }

    [Theory]
    [InlineData("\"files\":{\"name\":\"OVS_P.pak\"}", "could not be converted")]
    [InlineData("\"files\":[\"OVS_P.pak\"]", "could not be converted")]
    [InlineData("\"files\":[{\"name\":\"OVS_P.pak\",\"kind\":\"paks\",\"size\":\"12 KB\"}]", "\"12 KB\"")]
    [InlineData("\"files\":[{\"name\":\"OVS_P.pak\",\"kind\":\"paks\",\"size\":12.5}]", "whole number")]
    public void AFileListThatCannotBeReadIsReportedRatherThanTakenForNoPaks(string files, string reason)
    {
        var told = new List<string>();
        var http = new ServedFiles { [VersionUrl] = Bytes($"{{\"latest_version\":\"{OvsVersion.Current}\",\"is_latest\":true,{files}}}") };

        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, told, []).Run());
        Assert.Empty(told);
        string warning = Assert.Single(_log.Lines, l => l.Contains("Not using this release's paks"));
        Assert.Contains("its file list cannot be read", warning);
        Assert.Contains(reason, warning);
    }

    [Fact]
    public void NoServerMeansTheBackgroundCheckRuns()
    {
        var told = new List<string>();
        Assert.Equal(StartupUpdate.Outcome.NoAnswer, Startup(new ServedFiles(), told, []).Run());
        Assert.Empty(told);
    }

    [Fact]
    public void DuePaksAreInstalledAndTheGameCloses()
    {
        byte[] pak = Bytes("endgame content");
        var entry = Entry("OVS_P.pak", pak);
        var told = new List<string>();
        var notices = new List<string>();
        var http = new ServedFiles { [VersionUrl] = VersionJson(entry), [entry.DownloadUrl!] = pak };

        Assert.Equal(StartupUpdate.Outcome.Installed, Startup(http, told, notices).Run());
        Assert.Equal(pak, File.ReadAllBytes(Path.Combine(_paks, "OVS_P.pak")));
        Assert.Equal(["Update complete", "exit"], told);
        Assert.Equal(["New game content installed"], notices);

        // The next launch finds nothing to do.
        told.Clear();
        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, told, []).Run());
        Assert.Empty(told);
    }

    [Fact]
    public void AFailedPakDownloadClosesTheGameInsteadOfCrashingIt()
    {
        var entry = Entry("OVS_P.pak", Bytes("endgame content"));
        var told = new List<string>();
        var notices = new List<string>();
        var http = new ServedFiles { [VersionUrl] = VersionJson(entry) };

        Assert.Equal(StartupUpdate.Outcome.Failed, Startup(http, told, notices).Run());
        Assert.Equal(["Update failed", "exit"], told);
        Assert.Empty(notices);
        Assert.False(File.Exists(Path.Combine(_paks, "OVS_P.pak")));
    }

    [Fact]
    public void AnUnsafeManifestIsIgnored()
    {
        var entry = Entry("OVS_P.pak", Bytes("x"), url: "https://example.com/OVS_P.pak");
        var told = new List<string>();
        var http = new ServedFiles { [VersionUrl] = VersionJson(entry) };

        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, told, []).Run());
        Assert.Empty(told);
        Assert.Contains(_log.Lines, l => l.Contains("Not using this release's paks"));
    }

    // The backup manifest: an install the process dies in is put back on the next launch

    [Fact]
    public void AnUnfinishedManifestWithoutFilesIsReportedNotThrown()
    {
        var updater = Updater(new ServedFiles());
        Directory.CreateDirectory(Backup);
        File.WriteAllText(updater.ManifestPath, "{\"state\":\"installing\",\"release\":\"x\",\"started\":\"y\"}");

        Assert.False(updater.Recover());
        Assert.Contains(_log.Lines, l => l.Contains("lists no files"));
    }

    private PakInstallManifest ReadManifest(PakUpdate updater) =>
        System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(updater.ManifestPath), OvsJson.Default.PakInstallManifest)!;

    /// <summary>OVS_A and OVS_B installed; the release changes both and adds OVS_C. Returns the release files, already downloaded.</summary>
    private List<UpdateFile> StageABC(out ServedFiles http)
    {
        File.WriteAllBytes(Path.Combine(_paks, "OVS_A.pak"), Bytes("old a"));
        File.WriteAllBytes(Path.Combine(_paks, "OVS_B.pak"), Bytes("old b"));
        byte[] a = Bytes("new a"), b = Bytes("new b"), c = Bytes("new c");
        var files = new List<UpdateFile> { Entry("OVS_A.pak", a), Entry("OVS_B.pak", b), Entry("OVS_C.pak", c) };
        http = new ServedFiles { [Release + "OVS_A.pak"] = a, [Release + "OVS_B.pak"] = b, [Release + "OVS_C.pak"] = c };
        Assert.True(Updater(http).Download(files));
        return files;
    }

    [Fact]
    public void AnInstallLeavesAManifestOfWhatItReplaced()
    {
        var files = StageABC(out var http);
        var updater = Updater(http);

        Assert.True(updater.Install(files, "2026.09.27.1"));
        var manifest = ReadManifest(updater);
        Assert.Equal("complete", manifest.State);
        Assert.Equal("2026.09.27.1", manifest.Release);
        Assert.Equal(["OVS_A.pak", "OVS_B.pak", "OVS_C.pak"], manifest.Files.Select(f => f.Name));
        Assert.Equal(Sha(Bytes("old a")), manifest.Files[0].OldSha256);
        Assert.Equal(5, manifest.Files[0].OldSize);
        Assert.False(manifest.Files[2].HadOriginal);
        Assert.Null(manifest.Files[2].OldSha256);
        Assert.Equal("old a", File.ReadAllText(Path.Combine(Backup, "OVS_A.pak")));
        Assert.False(File.Exists(updater.ManifestPath + ".tmp"));

        // A finished install is not undone.
        Assert.True(Updater(http).Recover());
        Assert.Equal("new a", File.ReadAllText(Path.Combine(_paks, "OVS_A.pak")));
    }

    [Fact]
    public void AnInstallTheProcessDiesInIsPutBackOnTheNextLaunch()
    {
        var files = StageABC(out var http);
        var crashing = new PakUpdate(_paks, Staging, Backup, Path.Combine(_plugin, "PakHashes.txt"), http, _log)
        {
            AfterFileInstalled = name =>
            {
                if (name == "OVS_B.pak")
                {
                    throw new InvalidOperationException("process killed");
                }
            },
        };

        Assert.Throws<InvalidOperationException>(() => crashing.Install(files, "2026.09.27.1"));
        Assert.Equal("installing", ReadManifest(crashing).State);
        Assert.Equal("new b", File.ReadAllText(Path.Combine(_paks, "OVS_B.pak")));

        // The next launch, a new process: only the manifest on disk says what happened.
        var next = Updater(http);
        Assert.True(next.Recover());
        Assert.Equal("old a", File.ReadAllText(Path.Combine(_paks, "OVS_A.pak")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(_paks, "OVS_B.pak")));
        Assert.False(File.Exists(Path.Combine(_paks, "OVS_C.pak")));
        Assert.Equal("rolledback", ReadManifest(next).State);
        Assert.True(next.Recover()); // nothing left to do
    }

    [Fact]
    public void OnlyAVerifiedBackupIsRestored()
    {
        var files = StageABC(out var http);
        var crashing = new PakUpdate(_paks, Staging, Backup, Path.Combine(_plugin, "PakHashes.txt"), http, _log)
        {
            AfterFileInstalled = name => throw new InvalidOperationException("process killed"),
        };
        Assert.Throws<InvalidOperationException>(() => crashing.Install(files));
        File.WriteAllText(Path.Combine(Backup, "OVS_A.pak"), "old A"); // same size, different bytes

        var next = Updater(http);
        Assert.False(next.Recover());
        Assert.Equal("new a", File.ReadAllText(Path.Combine(_paks, "OVS_A.pak")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(_paks, "OVS_B.pak"))); // never moved
        Assert.Equal("rollback-failed", ReadManifest(next).State);
        Assert.Contains(_log.Lines, l => l.Contains("OVS_A.pak: no copy matching the recorded SHA-256"));

        // The release check then brings the pak folder to the release: OVS_A is already the
        // release's copy, so only the rest is downloaded.
        Assert.Equal(["OVS_B.pak", "OVS_C.pak"], next.Needed(files).Select(f => f.Name));
    }

    [Fact]
    public void ADeadInstallIsPutBackEvenWhenTheServerIsDown()
    {
        var files = StageABC(out var http);
        var crashing = new PakUpdate(_paks, Staging, Backup, Path.Combine(_plugin, "PakHashes.txt"), http, _log)
        {
            AfterFileInstalled = name => throw new InvalidOperationException("process killed"),
        };
        Assert.Throws<InvalidOperationException>(() => crashing.Install(files));
        Assert.Equal("new a", File.ReadAllText(Path.Combine(_paks, "OVS_A.pak")));

        Assert.Equal(StartupUpdate.Outcome.NoAnswer, Startup(new ServedFiles(), [], []).Run());
        Assert.Equal("old a", File.ReadAllText(Path.Combine(_paks, "OVS_A.pak")));
    }

    // Hand installs in the game's Content\Paks, which outranks Saved\Paks

    [Fact]
    public void AMatchingHandInstallIsMovedOverNotDownloaded()
    {
        byte[] pak = Bytes("current content");
        var entry = Entry("OVS_P.pak", pak);
        File.WriteAllBytes(Path.Combine(_content, "OVS_P.pak"), pak);
        File.WriteAllBytes(Path.Combine(_content, "pakchunk0-Windows.pak"), Bytes("the game's own"));
        var told = new List<string>();
        var http = new ServedFiles { [VersionUrl] = VersionJson(entry), [entry.DownloadUrl!] = pak };

        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, told, []).Run());
        Assert.Empty(told);
        Assert.Equal([VersionUrl], http.Requested);
        Assert.Equal(pak, File.ReadAllBytes(Path.Combine(_paks, "OVS_P.pak")));
        Assert.False(File.Exists(Path.Combine(_content, "OVS_P.pak")));
        Assert.True(File.Exists(Path.Combine(_content, "pakchunk0-Windows.pak")));
    }

    [Fact]
    public void AStaleHandInstallIsMovedAsideAndTheReleaseDownloaded()
    {
        byte[] pak = Bytes("current content");
        var entry = Entry("OVS_P.pak", pak);
        File.WriteAllBytes(Path.Combine(_content, "OVS_P.pak"), Bytes("last month's"));
        File.WriteAllBytes(Path.Combine(_content, "OVS_Old.utoc"), Bytes("no longer released"));
        var told = new List<string>();
        var http = new ServedFiles { [VersionUrl] = VersionJson(entry), [entry.DownloadUrl!] = pak };

        Assert.Equal(StartupUpdate.Outcome.Installed, Startup(http, told, []).Run());
        Assert.Equal(pak, File.ReadAllBytes(Path.Combine(_paks, "OVS_P.pak")));
        Assert.Empty(Directory.EnumerateFiles(_content));
        Assert.Equal("last month's", File.ReadAllText(Path.Combine(OldContentBackup, "OVS_P.pak")));
        Assert.True(File.Exists(Path.Combine(OldContentBackup, "OVS_Old.utoc")));
    }

    [Fact]
    public void AReleaseWithoutPaksLeavesHandInstallsAlone()
    {
        File.WriteAllBytes(Path.Combine(_content, "OVS_P.pak"), Bytes("hand installed"));
        var http = new ServedFiles { [VersionUrl] = VersionJson() };

        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, [], []).Run());
        Assert.True(File.Exists(Path.Combine(_content, "OVS_P.pak")));
    }

    [Fact]
    public void AStrayHandInstallIsMovedAsideEvenWhenNothingDownloads()
    {
        byte[] pak = Bytes("current content");
        var entry = Entry("OVS_P.pak", pak);
        File.WriteAllBytes(Path.Combine(_paks, "OVS_P.pak"), pak);
        File.WriteAllBytes(Path.Combine(_content, "OVS_P.pak"), Bytes("a stale copy that would win"));
        var told = new List<string>();
        var http = new ServedFiles { [VersionUrl] = VersionJson(entry) };

        Assert.Equal(StartupUpdate.Outcome.UpToDate, Startup(http, told, []).Run());
        Assert.Empty(told);
        Assert.False(File.Exists(Path.Combine(_content, "OVS_P.pak")));
        Assert.True(File.Exists(Path.Combine(OldContentBackup, "OVS_P.pak")));
    }

    /// <summary>Serves fixed bodies by URL (404 otherwise) and records what was asked for.</summary>
    private sealed class ServedFiles : Dictionary<string, byte[]>, IHttpTransport
    {
        public List<string> Requested { get; } = [];
        /// <summary>How many requests get no response before the served ones start.</summary>
        public int FailFirst { get; set; }

        public HttpResult Get(Uri url, TimeSpan timeout)
        {
            Requested.Add(url.ToString());
            if (FailFirst > 0)
            {
                FailFirst--;
                return HttpResult.Failed("connection reset");
            }

            return TryGetValue(url.ToString(), out var body) ? new HttpResult(true, 200, body, null) : new HttpResult(false, 404, [], null);
        }

        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout) => throw new NotSupportedException();
    }
}
