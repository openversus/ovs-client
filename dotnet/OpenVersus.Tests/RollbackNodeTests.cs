using System.Buffers.Binary;
using OpenVersus.P2P;

namespace OpenVersus.Tests;

public class RollbackNodeTests
{
    private const string Mod = @"C:\Game\plugins\OpenVersus";
    private const string System32 = @"C:\windows\system32";
    private static readonly string WindowsExe = NodeFiles.WindowsPath(Mod);
    private static readonly string LinuxExe = NodeFiles.LinuxPath(Mod);

    private static Func<string, bool> Files(params string[] present) => path => present.Contains(path);
    private static readonly NodeOptions Options = new(0, Path.Combine(Mod, "node", "port-4242.txt"), 0xDEADBEEF, 10, "https://prod.openversus.org/");
    private static string Unix(string windows) => "/home/deck/game/plugins/OpenVersus/" + windows[(Mod.Length + 1)..].Replace('\\', '/');

    [Theory]
    [InlineData("p2p.openversus.org", 41235UL, "p2p.openversus.org:41235")]
    [InlineData("  p2p.openversus.org  ", 5000UL, "p2p.openversus.org:5000")]
    [InlineData("203.0.113.9", 1UL, "203.0.113.9:1")]
    [InlineData("203.0.113.9", 65535UL, "203.0.113.9:65535")]
    [InlineData("p2p.openversus.org", 0UL, "p2p.openversus.org:41235")]
    [InlineData("p2p.openversus.org", 65536UL, "p2p.openversus.org:41235")]
    [InlineData("p2p.openversus.org", ulong.MaxValue, "p2p.openversus.org:41235")]
    [InlineData("", 41235UL, "")]
    [InlineData("   ", 0UL, "")]
    [InlineData(null, 70000UL, "")]
    public void TheRegistryHostAndPortBecomeTheNodesRendezvous_AnUnusablePortFallsBackToTheDefault(string? host, ulong port, string expected) =>
        Assert.Equal(expected, RendezvousAddress.Normalize(host, port));

    [Theory]
    [InlineData("p2p.openversus.org:41235")]
    [InlineData(":41235")]
    [InlineData("p2p.open versus.org")]
    [InlineData("p2p.openversus.org\"")]
    public void AHostTheNodeCannotTakeIsRefused(string host) => Assert.Null(RendezvousAddress.Normalize(host, 41235));

    [Fact]
    public void TheRegistryPortRowDefaultsToTheRendezvousPort_AndAnUnparseableValueReadsAsIt()
    {
        Assert.Equal("41235", OpenVersus.Config.Settings.Rows.P2PRegistryPort.Default);
        var s = OpenVersus.Config.Settings.FromValues(new Dictionary<OpenVersus.Config.SettingDef, string> { [OpenVersus.Config.Settings.Rows.P2PRegistryPort] = "forty" });
        Assert.Equal(41235UL, s.P2PRegistryPort);
    }

    [Fact]
    public void TheRendezvousIsPassedOnlyWhenThereIsOne()
    {
        Assert.DoesNotContain("--rendezvous", Options.Arguments("/p"));
        Assert.EndsWith(" --rendezvous \"p2p.openversus.org:41235\"", (Options with { Rendezvous = "p2p.openversus.org:41235" }).Arguments("/p"));
        Assert.Null(NodeLaunch.Plan(Mod, underWine: false, Options with { Rendezvous = "a\"b:1" }, Files(WindowsExe), _ => null, System32, out string why));
        Assert.Contains("quote", why);
    }

    [Fact]
    public void TheLogFileIsPassedOnlyWhenThereIsOne_AndAnOtherPathCanStandInForIt()
    {
        var logged = Options with { LogFile = Path.Combine(Mod, "logs", "RollbackNode.log") };
        Assert.DoesNotContain("--log-file", Options.Arguments("/p"));
        Assert.EndsWith($" --log-file \"{logged.LogFile}\"", logged.Arguments("/p"));
        Assert.EndsWith(" --log-file \"/l\"", logged.Arguments("/p", "/l"));
        Assert.DoesNotContain("--log-file", logged.Arguments("/p", ""));
        Assert.Null(NodeLaunch.Plan(Mod, underWine: false, Options with { LogFile = "C:\\odd\\\"\\RollbackNode.log" }, Files(WindowsExe), _ => null, System32, out string why));
        Assert.Contains("quote", why);
    }

    [Fact]
    public void TheLogFileReachesEitherNodeAsAPathItsSystemReads()
    {
        var logged = Options with { LogFile = Path.Combine(Mod, "logs", "RollbackNode.log") };
        var windows = NodeLaunch.Plan(Mod, underWine: false, logged, Files(WindowsExe), _ => null, System32, out string why);
        Assert.NotNull(windows);
        Assert.Equal("", why);
        Assert.EndsWith($" --log-file \"{logged.LogFile}\"", windows.Arguments);

        var linux = NodeLaunch.Plan(Mod, underWine: true, logged, Files(WindowsExe, LinuxExe), Unix, System32, out why);
        Assert.NotNull(linux);
        Assert.Equal("", why);
        Assert.EndsWith(" --log-file \"/home/deck/game/plugins/OpenVersus/logs/RollbackNode.log\"", linux.Arguments);

        // Wine converts the node and its port file but not the log file: still the Linux node, without the option.
        linux = NodeLaunch.Plan(Mod, underWine: true, logged, Files(WindowsExe, LinuxExe), p => p == logged.LogFile ? null : Unix(p), System32, out why);
        Assert.NotNull(linux);
        Assert.False(linux.IsNodeItself);
        Assert.DoesNotContain("--log-file", linux.Arguments);
        Assert.Contains(logged.LogFile, why);
    }

    [Fact]
    public void TheOptionsBecomeTheNodesCommandLine()
    {
        // The tests run on Linux too, where Path.Combine joins with '/', so the path is taken from the options.
        Assert.Equal(
            $"0 --port-file \"{Options.PortFile}\" --parent-token 3735928559 --parent-timeout 10 --server \"https://prod.openversus.org/\"",
            Options.Arguments(Options.PortFile));
        Assert.StartsWith("41234 --port-file \"/tmp/p.txt\" --parent-token 18446744073709551615 ", (Options with { Port = 41234, ParentToken = ulong.MaxValue }).Arguments("/tmp/p.txt"));
    }

    [Fact]
    public void OnWindowsTheWindowsNodeRunsFromItsFolderWithTheOptions()
    {
        var plan = NodeLaunch.Plan(Mod, underWine: false, Options, Files(WindowsExe, LinuxExe), _ => throw new Xunit.Sdk.XunitException("no Wine, no path conversion"), System32, out string why);
        Assert.NotNull(plan);
        Assert.Equal("", why);
        Assert.Equal(WindowsExe, plan.Executable);
        Assert.Equal(Options.Arguments(Options.PortFile), plan.Arguments);
        Assert.Equal(Path.GetDirectoryName(WindowsExe), plan.WorkingDirectory);
        Assert.True(plan.IsNodeItself);
        Assert.Equal($"\"{WindowsExe}\" {plan.Arguments}", plan.CommandLine);
    }

    [Fact]
    public void UnderWineTheLinuxNodeIsPreferredAndStartedThroughStartExeWithLinuxPaths()
    {
        var plan = NodeLaunch.Plan(Mod, underWine: true, Options with { Port = 41234 }, Files(WindowsExe, LinuxExe), Unix, System32, out string why);
        Assert.NotNull(plan);
        Assert.Equal("", why);
        Assert.Equal(Path.Combine(System32, "start.exe"), plan.Executable);
        Assert.Equal("/unix \"/home/deck/game/plugins/OpenVersus/node/linux-x64/OVS.Rollback.Node\" 41234 --port-file \"/home/deck/game/plugins/OpenVersus/node/port-4242.txt\" --parent-token 3735928559 --parent-timeout 10 --server \"https://prod.openversus.org/\"", plan.Arguments);
        Assert.Equal(Path.GetDirectoryName(LinuxExe), plan.WorkingDirectory);
        Assert.False(plan.IsNodeItself);
    }

    [Fact]
    public void UnderWineWithoutALinuxNodeTheWindowsOneRuns()
    {
        var plan = NodeLaunch.Plan(Mod, underWine: true, Options, Files(WindowsExe), _ => "/unused", System32, out string why);
        Assert.NotNull(plan);
        Assert.Equal("", why);
        Assert.Equal(WindowsExe, plan.Executable);
        Assert.True(plan.IsNodeItself);
        Assert.Contains("under Wine", plan.Description);
    }

    [Fact]
    public void UnderWineAnUnconvertiblePathFallsBackToTheWindowsNodeAndSaysSo()
    {
        var plan = NodeLaunch.Plan(Mod, underWine: true, Options, Files(WindowsExe, LinuxExe), _ => null, System32, out string why);
        Assert.NotNull(plan);
        Assert.Equal(WindowsExe, plan.Executable);
        Assert.Contains("Linux path", why);
        Assert.Contains(LinuxExe, why);

        // The node converts but the port file does not: same fallback, naming the port file.
        plan = NodeLaunch.Plan(Mod, underWine: true, Options, Files(WindowsExe, LinuxExe), p => p == LinuxExe ? Unix(p) : null, System32, out why);
        Assert.NotNull(plan);
        Assert.Equal(WindowsExe, plan.Executable);
        Assert.Contains(Options.PortFile, why);

        var quoted = NodeLaunch.Plan(Mod, underWine: true, Options, Files(WindowsExe, LinuxExe), _ => "/home/a\"b/node", System32, out why);
        Assert.NotNull(quoted);
        Assert.Equal(WindowsExe, quoted.Executable);
        Assert.Contains("quote", why);

        Assert.Null(NodeLaunch.Plan(Mod, underWine: true, Options, Files(LinuxExe), _ => null, System32, out why));
    }

    [Fact]
    public void AQuoteInAnOptionRefusesToStartOnAnyPlatform()
    {
        Assert.Null(NodeLaunch.Plan(Mod, underWine: false, Options with { PortFile = "C:\\odd\\\"\\port.txt" }, Files(WindowsExe), _ => null, System32, out string why));
        Assert.Contains("quote", why);
        Assert.Null(NodeLaunch.Plan(Mod, underWine: false, Options with { ServerUrl = "http://x/\"" }, Files(WindowsExe), _ => null, System32, out why));
    }

    [Fact]
    public void WithNoNodeThereIsNoPlanAndTheReasonNamesBothPaths()
    {
        Assert.Null(NodeLaunch.Plan(Mod, underWine: true, Options, Files(), _ => null, System32, out string why));
        Assert.Contains(WindowsExe, why);
        Assert.Contains(LinuxExe, why);
        Assert.Null(NodeLaunch.Plan(Mod, underWine: false, Options, Files(LinuxExe), _ => null, System32, out why));
        Assert.DoesNotContain(LinuxExe, why);
    }

    [Theory]
    [InlineData("41234\n", 41234)]
    [InlineData("  65535 \r\n", 65535)]
    [InlineData("1", 1)]
    public void ThePortFileIsOneNumber(string text, int port) => Assert.Equal((ushort)port, PortFile.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("4123x")]
    [InlineData("41234 41235")]
    public void AnythingElseInThePortFileIsNoPort(string? text) => Assert.Null(PortFile.Parse(text));

    [Fact]
    public void ThePortFileIsNamedForTheGameProcess()
    {
        Assert.Equal(Path.Combine(Mod, "node", "port-4242.txt"), PortFile.PathFor(Mod, 4242));
        Assert.NotEqual(PortFile.PathFor(Mod, 1), PortFile.PathFor(Mod, 2));
    }

    [Fact]
    public void SweepingRemovesOnlyOldPortFiles()
    {
        string mod = Path.Combine(Path.GetTempPath(), "ovs-node-sweep-" + Guid.NewGuid().ToString("N"));
        string node = Path.Combine(mod, "node");
        Directory.CreateDirectory(node);
        try
        {
            string old = Path.Combine(node, "port-1.txt");
            string fresh = Path.Combine(node, "port-2.txt");
            string other = Path.Combine(node, "node.appsettings.json");
            File.WriteAllText(old, "1\n");
            File.WriteAllText(fresh, "2\n");
            File.WriteAllText(other, "{}");
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(old, now - TimeSpan.FromHours(2));

            Assert.Equal(1, PortFile.Sweep(mod, now, TimeSpan.FromHours(1)));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));
            Assert.Equal(0, PortFile.Sweep(Path.Combine(mod, "missing"), now, TimeSpan.FromHours(1)));
        }
        finally
        {
            Directory.Delete(mod, recursive: true);
        }
    }

    [Fact]
    public void TheKeepaliveIsTheNodesParentMessage()
    {
        // The node's P2P protocol: "OVSP2P", version 1, kind 6 (Parent), then the token little-endian.
        // Checked against the node on 2026-10-03: a node started with Node__ParentToken=42 stayed up on
        // these bytes and exited on the same bytes with token 43.
        byte[] bytes = ParentKeepAlive.Encode(0x0102030405060708);
        Assert.Equal(16, bytes.Length);
        Assert.Equal("OVSP2P"u8.ToArray(), bytes[..6]);
        Assert.Equal(1, bytes[6]);
        Assert.Equal(6, bytes[7]);
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, bytes[8..]);
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(ParentKeepAlive.Encode(ulong.MaxValue).AsSpan(8)));
    }
}
