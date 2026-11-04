using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpenVersus.Net;
using OpenVersus.P2P;

namespace OpenVersus.Tests;

public sealed class NodeUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ovs-node-").FullName;
    private string Mod => Path.Combine(_root, "plugins", "OpenVersus");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ── The version marker ──

    [Fact]
    public void TheVersionIsReadFromTheMarkerAndNothingElseCounts()
    {
        Assert.Equal("2026.11.04.01", NodeVersion.Find("xx\0OVS-NODE-VERSION:2026.11.04.01;yy"u8));
        Assert.Equal("", NodeVersion.Find("no marker here"u8));
        Assert.Equal("", NodeVersion.Find("OVS-NODE-VERSION:unterminated"u8));
        Assert.Equal("", NodeVersion.Find("OVS-NODE-VERSION:;"u8));
        Assert.Equal("", NodeVersion.Find("OVS-NODE-VERSION:has space;"u8));
        // A broken first marker does not hide a good later one.
        Assert.Equal("1.2.3-rc_1", NodeVersion.Find("OVS-NODE-VERSION:bad one;OVS-NODE-VERSION:1.2.3-rc_1;"u8));
    }

    [Fact]
    public void TheVersionIsFoundAcrossTheScanBoundaryAndAMissingFileHasNone()
    {
        string path = Path.Combine(_root, "node.bin");
        byte[] bytes = new byte[3 * (1 << 20)];
        byte[] marker = Encoding.ASCII.GetBytes("OVS-NODE-VERSION:2026.11.04.01;");
        // Straddles the first megabyte.
        marker.CopyTo(bytes, (1 << 20) - 7);
        File.WriteAllBytes(path, bytes);
        Assert.Equal("2026.11.04.01", NodeVersion.Read(path));
        Assert.Equal("", NodeVersion.Read(Path.Combine(_root, "absent.bin")));
        File.WriteAllBytes(path, new byte[100]);
        Assert.Equal("", NodeVersion.Read(path));
    }

    // ── The platform ──

    [Fact]
    public void ThePlatformFollowsWhatTheGameRunsOn()
    {
        Assert.Equal(["win-x64"], NodePlatform.Wanted(underWine: false, onMac: false, null));
        Assert.Equal(["linux-x64"], NodePlatform.Wanted(underWine: true, onMac: false, null));
        Assert.Equal(["osx-arm64"], NodePlatform.Wanted(underWine: true, onMac: true, "osx-arm64"));
        Assert.Equal(["osx-x64"], NodePlatform.Wanted(underWine: true, onMac: true, "osx-x64"));
        Assert.Equal(["osx-arm64", "osx-x64"], NodePlatform.Wanted(underWine: true, onMac: true, null));
        Assert.Equal(Path.Combine(Mod, "node", "win-x64", "OVS.Rollback.Node.exe"), NodePlatform.ExecutablePath(Mod, "win-x64"));
        Assert.Equal(Path.Combine(Mod, "node", "osx-arm64", "OVS.Rollback.Node"), NodePlatform.ExecutablePath(Mod, "osx-arm64"));
    }

    [Fact]
    public void TheMacIsAskedThroughAScriptAndItsAnswerIsRead()
    {
        var log = new ListLogger();
        string? commandLine = null;
        string? platform = MacArchitecture.Probe(Mod, w => "/unix/" + Path.GetFileName(w), @"C:\windows\system32", (cl, dir) =>
        {
            commandLine = cl;
            File.WriteAllText(Path.Combine(Mod, "node", $"arch-{Environment.ProcessId}.txt"), "osx-arm64\n");
            return true;
        }, log);
        Assert.Equal("osx-arm64", platform);
        Assert.Equal($"\"{Path.Combine(@"C:\windows\system32", "start.exe")}\" /unix /bin/sh \"/unix/mac-arch.sh\" \"/unix/arch-{Environment.ProcessId}.txt\"", commandLine);
        Assert.Contains("sysctl -n hw.optional.arm64", File.ReadAllText(Path.Combine(Mod, "node", "mac-arch.sh")));
        Assert.False(File.Exists(Path.Combine(Mod, "node", $"arch-{Environment.ProcessId}.txt")));
        // No answer in time, or one that is not a platform, or no process: null, and said in the log.
        Assert.Null(MacArchitecture.Probe(Mod, w => "/unix/" + Path.GetFileName(w), @"C:\windows\system32", (cl, dir) => true, log, sleep: _ => { }));
        Assert.Null(MacArchitecture.Probe(Mod, w => "/unix/" + Path.GetFileName(w), @"C:\windows\system32", (cl, dir) => false, log));
        Assert.Null(MacArchitecture.Parse("amd64"));
        Assert.Contains(log.Lines, l => l.Contains("gave no answer"));
    }

    // ── What is needed ──

    private static VersionInfo Info(bool nodeIsLatest, string nodeVersion = "2026.11.04.01") => new("2026.11.04.01", "", true, "", nodeVersion, nodeIsLatest);

    private static UpdateFile NodeFile(string platform, string version = "2026.11.04.01", string owner = "openversus", string? sha = null) =>
        new($"OVS_Node_{version}_{platform}.{(platform == "win-x64" ? "zip" : "tar.gz")}", "node", 100, sha ?? new string('a', 64),
            $"https://github.com/{owner}/ovs-client/releases/download/{version}/OVS_Node_{version}_{platform}.{(platform == "win-x64" ? "zip" : "tar.gz")}", platform);

    [Fact]
    public void OnlyThisPlatformsArchiveIsNeededAndOnlyWhenTheServerSaysSo()
    {
        var log = new ListLogger();
        var files = new ReleaseFiles([NodeFile("win-x64"), NodeFile("linux-x64"), new UpdateFile("OVS_P.pak", "paks", 5, new string('b', 64), "https://github.com/openversus/ovs-client/releases/download/1/OVS_P.pak")]);
        var windows = new NodeUpdate(Mod, new NoServer(), log, ["win-x64"], null, @"C:\windows\system32");
        Assert.Equal(["OVS_Node_2026.11.04.01_win-x64.zip"], windows.Needed(Info(nodeIsLatest: false), files).Select(f => f.Name));
        Assert.Empty(windows.Needed(Info(nodeIsLatest: true), files));
        Assert.Empty(windows.Needed(Info(nodeIsLatest: false), new ReleaseFiles([files.Files![2]])));
        Assert.Empty(windows.Needed(Info(nodeIsLatest: false), null));
        // A Mac of unknown architecture wants both builds; the release has neither here.
        var mac = new NodeUpdate(Mod, new NoServer(), log, ["osx-arm64", "osx-x64"], w => w, @"C:\windows\system32");
        Assert.Empty(mac.Needed(Info(nodeIsLatest: false), files));
        Assert.Contains(log.Lines, l => l.Contains("no node for osx-arm64"));
        // Not from the release owner, or without a hash: refused.
        Assert.Empty(windows.Needed(Info(nodeIsLatest: false), new ReleaseFiles([NodeFile("win-x64", owner: "someone")])));
        Assert.Empty(windows.Needed(Info(nodeIsLatest: false), new ReleaseFiles([NodeFile("win-x64", sha: "")])));
    }

    // ── Download and install ──

    private static byte[] FakeNode(string version) => Encoding.ASCII.GetBytes($"ELF\0...OVS-NODE-VERSION:{version};...");

    private static byte[] Zip(string platform, string version)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, $"node/{platform}/OVS.Rollback.Node.exe", FakeNode(version));
            Write(zip, $"node/{platform}/node.appsettings.json", "{}"u8.ToArray());
        }

        return stream.ToArray();

        static void Write(ZipArchive zip, string name, byte[] bytes)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(bytes);
        }
    }

    private static byte[] TarGz(string platform, string version, bool withLauncher = true)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "node/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"node/{platform}/"));
            Write(tar, $"node/{platform}/OVS.Rollback.Node", FakeNode(version));
            Write(tar, $"node/{platform}/node.appsettings.json", "{}"u8.ToArray());
            if (withLauncher)
            {
                Write(tar, "node/OVS.Rollback.Node.sh", "#!/bin/sh\necho launcher\n"u8.ToArray());
            }
        }

        return stream.ToArray();

        static void Write(TarWriter tar, string name, byte[] bytes)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) };
            tar.WriteEntry(entry);
        }
    }

    private static UpdateFile Served(ServedFiles server, string platform, byte[] archive, string version = "2026.11.04.01")
    {
        var file = NodeFile(platform, version, sha: Convert.ToHexStringLower(SHA256.HashData(archive))) with { Size = archive.Length };
        server[file.DownloadUrl!] = archive;
        return file;
    }

    [Fact]
    public void TheWindowsNodeIsDownloadedVerifiedUnpackedAndSwappedInKeepingThePreviousOne()
    {
        var log = new ListLogger();
        var server = new ServedFiles();
        var file = Served(server, "win-x64", Zip("win-x64", "2026.11.04.01"));
        string target = Path.Combine(Mod, "node", "win-x64");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "OVS.Rollback.Node.exe"), FakeNode("2026.10.01.01"));
        var update = new NodeUpdate(Mod, server, log, ["win-x64"], null, @"C:\windows\system32") { Sleep = _ => { }, StartProcess = (cl, dir) => throw new Xunit.Sdk.XunitException("no Wine, no chmod") };
        Assert.Equal("2026.10.01.01", update.InstalledVersion());

        var progress = new List<string>();
        long bytes = 0;
        Assert.True(update.Download([file], (name, n, count) => progress.Add($"{name} {n}/{count}"), b => bytes += b));
        Assert.Equal(["OVS_Node_2026.11.04.01_win-x64.zip 1/1"], progress);
        Assert.Equal(file.Size, bytes);
        Assert.True(update.Install([file]));

        Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "node.appsettings.json")));
        Assert.Equal("2026.10.01.01", NodeVersion.Read(Path.Combine(target + ".old", "OVS.Rollback.Node.exe")));
        Assert.False(Directory.Exists(update.StagingDirectory));
        // The next launch reports the new version and, with it in place, lets the previous build go.
        Assert.Equal("2026.11.04.01", update.InstalledVersion());
        Assert.False(Directory.Exists(target + ".old"));
        Assert.Contains(log.Lines, l => l.Contains("Installed the win-x64 node 2026.11.04.01"));
    }

    [Fact]
    public void ADownloadThatDoesNotMatchTheReleaseIsRetriedThenRefusedAndNothingChanges()
    {
        var log = new ListLogger();
        var server = new ServedFiles();
        var file = Served(server, "win-x64", Zip("win-x64", "2026.11.04.01")) with { Sha256 = new string('c', 64) };
        var update = new NodeUpdate(Mod, server, log, ["win-x64"], null, @"C:\windows\system32") { Sleep = _ => { } };
        Assert.False(update.Download([file]));
        Assert.Equal(NodeUpdate.Attempts, server.Requested.Count);
        Assert.False(File.Exists(Path.Combine(update.StagingDirectory, file.Name!)));
        Assert.False(Directory.Exists(Path.Combine(Mod, "node", "win-x64")));
        Assert.Contains(log.Lines, l => l.Contains("its SHA-256 is"));
    }

    [Fact]
    public void UnderWineTheLinuxNodeAndTheLauncherGetTheirExecuteBitsThroughStartExe()
    {
        var log = new ListLogger();
        var server = new ServedFiles();
        var file = Served(server, "linux-x64", TarGz("linux-x64", "2026.11.04.01"));
        var chmods = new List<string>();
        string marker = Path.Combine(Mod, "node", $"chmod-{Environment.ProcessId}.txt");
        var update = new NodeUpdate(Mod, server, log, ["linux-x64"], w => "/unix/" + w[(Mod.Length + 1)..].Replace('\\', '/'), @"C:\windows\system32")
        {
            Sleep = _ => { },
            StartProcess = (cl, dir) => { chmods.Add(cl); File.WriteAllText(marker, "ok\n"); return true; },
        };
        Assert.True(update.Download([file]));
        Assert.True(update.Install([file]));
        Assert.Equal("2026.11.04.01", update.InstalledVersion());
        Assert.Equal("#!/bin/sh\necho launcher\n", File.ReadAllText(Path.Combine(Mod, "node", "OVS.Rollback.Node.sh")));
        Assert.Equal([$"\"{Path.Combine(@"C:\windows\system32", "start.exe")}\" /unix /bin/sh \"/unix/node/chmod.sh\" \"/unix/node/chmod-{Environment.ProcessId}.txt\" \"/unix/node/linux-x64/OVS.Rollback.Node\" \"/unix/node/OVS.Rollback.Node.sh\""], chmods);
        Assert.Equal(NodeUpdate.ChmodScriptText, File.ReadAllText(Path.Combine(Mod, "node", "chmod.sh")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void WhenTheExecuteBitsCannotBeSetThePreviousNodeIsPutBack()
    {
        var log = new ListLogger();
        var server = new ServedFiles();
        var file = Served(server, "linux-x64", TarGz("linux-x64", "2026.11.04.01"));
        string target = Path.Combine(Mod, "node", "linux-x64");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "OVS.Rollback.Node"), FakeNode("2026.10.01.01"));
        var update = new NodeUpdate(Mod, server, log, ["linux-x64"], w => "/unix/x", @"C:\windows\system32") { Sleep = _ => { }, StartProcess = (cl, dir) => false };
        Assert.True(update.Download([file]));
        Assert.False(update.Install([file]));
        Assert.Equal("2026.10.01.01", update.InstalledVersion());
        Assert.False(Directory.Exists(target + ".old"));
        Assert.Contains(log.Lines, l => l.Contains("putting the previous one back"));
    }

    [Fact]
    public void AVerifiedArchiveLeftByAFailedSwapIsReusedAndTheOldFolderIsSweptOnceTheNewNodeIsInPlace()
    {
        var log = new ListLogger();
        var server = new ServedFiles();
        var file = Served(server, "linux-x64", TarGz("linux-x64", "2026.11.04.01"));
        string target = Path.Combine(Mod, "node", "linux-x64");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "OVS.Rollback.Node"), FakeNode("2026.10.01.01"));
        // The chmod step never reports back: the swap is undone, but the download is kept.
        var failing = new NodeUpdate(Mod, server, log, ["linux-x64"], w => "/unix/x", @"C:\windows\system32") { Sleep = _ => { }, StartProcess = (cl, dir) => true };
        Assert.True(failing.Download([file]));
        Assert.False(failing.Install([file]));
        Assert.True(File.Exists(Path.Combine(failing.StagingDirectory, file.Name!)));
        Assert.False(Directory.Exists(Path.Combine(failing.StagingDirectory, "unpacked-linux-x64")));
        Assert.Equal(1, server.Requested.Count);

        // Next launch: no second download, and this time the chmod reports back.
        string marker = Path.Combine(Mod, "node", $"chmod-{Environment.ProcessId}.txt");
        var working = new NodeUpdate(Mod, server, log, ["linux-x64"], w => "/unix/x", @"C:\windows\system32") { Sleep = _ => { }, StartProcess = (cl, dir) => { File.WriteAllText(marker, "ok"); return true; } };
        Assert.True(working.Download([file]));
        Assert.Equal(1, server.Requested.Count);
        Assert.True(working.Install([file]));
        Assert.True(Directory.Exists(target + ".old"));
        Assert.False(Directory.Exists(working.StagingDirectory));

        // The launch after that sweeps the previous build, since the new one has its version.
        Assert.Equal("2026.11.04.01", working.InstalledVersion());
        Assert.False(Directory.Exists(target + ".old"));
    }

    [Fact]
    public void ANodeStillInUseIsLeftForTheNextLaunchWithTheDownloadKept()
    {
        var log = new ListLogger();
        var server = new ServedFiles();
        var file = Served(server, "win-x64", Zip("win-x64", "2026.11.04.01"));
        string target = Path.Combine(Mod, "node", "win-x64");
        Directory.CreateDirectory(target);
        string exe = Path.Combine(target, "OVS.Rollback.Node.exe");
        File.WriteAllBytes(exe, FakeNode("2026.10.01.01"));
        var update = new NodeUpdate(Mod, server, log, ["win-x64"], null, @"C:\windows\system32") { Sleep = _ => { } };
        Assert.True(update.Download([file]));
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(update.Install([file]));
        }

        Assert.Equal("2026.10.01.01", update.InstalledVersion());
        Assert.False(Directory.Exists(target + ".old"));
        Assert.True(File.Exists(Path.Combine(update.StagingDirectory, file.Name!)));
        Assert.Contains(log.Lines, l => l.Contains("still in use"));
    }

    [Fact]
    public void AnArchiveWithoutTheNodeOrWithAPathOutsideTheFolderIsRefused()
    {
        var log = new ListLogger();
        var server = new ServedFiles();
        var empty = Served(server, "linux-x64", TarGz("osx-x64", "2026.11.04.01"));
        var update = new NodeUpdate(Mod, server, log, ["linux-x64"], null, @"C:\windows\system32") { Sleep = _ => { } };
        Assert.True(update.Download([empty]));
        Assert.False(update.Install([empty]));
        Assert.Contains(log.Lines, l => l.Contains("holds no node/linux-x64/OVS.Rollback.Node"));

        string archive = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("../outside.txt");
        }
        Assert.Throws<InvalidDataException>(() => NodeUpdate.Extract(archive, Path.Combine(_root, "unpack")));
    }

    private sealed class NoServer : IHttpTransport
    {
        public HttpResult Get(Uri url, TimeSpan timeout) => throw new Xunit.Sdk.XunitException("no request expected");
        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout) => throw new NotSupportedException();
    }

    private sealed class ServedFiles : Dictionary<string, byte[]>, IHttpTransport
    {
        public List<string> Requested { get; } = [];

        public HttpResult Get(Uri url, TimeSpan timeout)
        {
            Requested.Add(url.ToString());
            return TryGetValue(url.ToString(), out var body) ? new HttpResult(true, 200, body, null) : new HttpResult(false, 404, [], null);
        }

        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout) => throw new NotSupportedException();
    }
}
