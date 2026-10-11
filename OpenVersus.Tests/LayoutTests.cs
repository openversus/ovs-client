using OpenVersus.Config;

namespace OpenVersus.Tests;

/// <summary>The move from plugins/ to plugins/OpenVersus/: what comes along and what stays.</summary>
public class LayoutTests : IDisposable
{
    private readonly string _plugins = Directory.CreateTempSubdirectory("ovs-layout-").FullName;
    private string Home => Path.Combine(_plugins, "OpenVersus");

    public LayoutTests() => Directory.CreateDirectory(Path.Combine(_plugins, "OpenVersus"));

    public void Dispose() => Directory.Delete(_plugins, recursive: true);

    private string Old(string name) => Path.Combine(_plugins, name);
    private string New(string name) => Path.Combine(Home, name);

    [Fact]
    public void TheModsFolderIsKnownByName()
    {
        Assert.True(Layout.IsHome(Home));
        Assert.True(Layout.IsHome(Path.Combine(_plugins, "openversus") + Path.DirectorySeparatorChar));
        Assert.False(Layout.IsHome(_plugins));
        Assert.Equal(_plugins, Layout.OldHome(Home));
        Assert.Null(Layout.OldHome(_plugins));
        Assert.Equal(Home, Layout.HomeFor(_plugins));
        Assert.Equal(Home, Layout.HomeFor(Home));
    }

    /// <summary>A C++ install: the ini, state and cache loose in plugins/, and someone else's logs.</summary>
    [Fact]
    public void TheCppClientsFilesComeAlongAndOtherModsLogsStay()
    {
        File.WriteAllText(Old("OpenVersus.ini"), "[Settings]\r\nAutoUpdate=false\r\n[Server.Game]\r\nServerUrl=http://203.0.113.7:8000/\r\n");
        File.WriteAllText(Old("OVSState.ini"), "[FirstRun]\r\nPaidModWarned=true\r\n");
        File.WriteAllText(Old("PatternsCache.cache"), "[X]\r\nA=1\r\n");
        Directory.CreateDirectory(Old("logs"));
        File.WriteAllText(Path.Combine(Old("logs"), "SpecialK.log"), "not ours");

        var s = Settings.Load(Home, null, Layout.OldHome(Home));
        var state = new State(Home, Layout.OldHome(Home)).Load();
        _ = new PatternCache(Home, 0xABCD1234_00000000, "2026.10.01.01", Layout.OldHome(Home));

        Assert.False(s.AutoUpdate);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Assert.True(state.PaidModWarned);
        Assert.True(File.Exists(New("OpenVersus.toml")));
        Assert.True(File.Exists(New("OVSState.toml")));
        Assert.False(File.Exists(Old("OpenVersus.ini")));
        Assert.False(File.Exists(Old("OVSState.ini")));
        Assert.False(File.Exists(Old("PatternsCache.cache")));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(Old("logs"), "SpecialK.log")));
        Assert.False(Directory.Exists(New("logs")));
    }

    /// <summary>A prerelease install: the TOML files loose in plugins/ move in as they are.</summary>
    [Fact]
    public void AnEarlierTomlLayoutMovesInAsItIs()
    {
        File.WriteAllText(Old("OpenVersus.toml"), "[Settings]\nAutoUpdate = false  # mine\n");
        File.WriteAllText(Old("OVSState.toml"), "[FirstRun]\nPaidModWarned = true\n");
        File.WriteAllText(Old("PatternsCache.toml"), "Exe = \"ABCD1234\"\n");
        var log = new ListLogger();

        var s = Settings.Load(Home, log, Layout.OldHome(Home));
        var state = new State(Home, Layout.OldHome(Home)).Load();
        _ = new PatternCache(Home, 0xABCD1234_00000000, "2026.10.01.01", Layout.OldHome(Home));

        Assert.False(s.AutoUpdate);
        Assert.True(state.PaidModWarned);
        Assert.StartsWith("[Settings]\nAutoUpdate = false  # mine\n", File.ReadAllText(New("OpenVersus.toml")));
        Assert.False(File.Exists(Old("OpenVersus.toml")));
        Assert.False(File.Exists(Old("OVSState.toml")));
        Assert.False(File.Exists(Old("PatternsCache.toml")));
        Assert.Contains(log.Lines, l => l.Contains("moved ") && l.Contains("OpenVersus.toml"));
    }

    /// <summary>A TOML already in the mod's folder wins; an old ini in plugins/ is merged into it.</summary>
    [Fact]
    public void AnOldIniBesideTheNewFolderIsMergedIn()
    {
        File.WriteAllText(New("OpenVersus.toml"), "[Settings]\nAutoUpdate = true\n");
        File.WriteAllText(Old("OpenVersus.toml"), "[Settings]\nAutoUpdate = false\n");
        File.WriteAllText(Old("OpenVersus.ini"), "[Settings.Debug]\r\nShowConsole=off\r\n");

        var s = Settings.Load(Home, null, Layout.OldHome(Home));
        Assert.True(s.AutoUpdate);
        Assert.False(s.EnableConsoleWindow);
        Assert.True(File.Exists(Old("OpenVersus.toml")));
        Assert.False(File.Exists(Old("OpenVersus.ini")));
    }

    /// <summary>A plugin still loose in plugins/ keeps working where it is.</summary>
    [Fact]
    public void APluginLooseInPluginsLooksNowhereElse()
    {
        File.WriteAllText(Old("OpenVersus.toml"), "[Settings]\nAutoUpdate = false\n");
        var s = Settings.Load(_plugins, null, Layout.OldHome(_plugins));
        Assert.False(s.AutoUpdate);
        Assert.False(File.Exists(New("OpenVersus.toml")));
    }

    /// <summary>A plugin running from somewhere odd moves into the mod's folder with its files;
    /// a logs folder beside it stays.</summary>
    [Fact]
    public void APluginElsewhereMovesInWithItsFilesButNotLogs()
    {
        string scripts = Path.Combine(_plugins, "scripts");
        Directory.CreateDirectory(Path.Combine(scripts, "logs"));
        string plugin = Path.Combine(scripts, "OpenVersus_2026.10.01.01.asi");
        File.WriteAllText(plugin, "plugin");
        File.WriteAllText(Path.Combine(scripts, "OpenVersus.toml"), "mine");
        File.WriteAllText(Path.Combine(scripts, "OVSState.toml"), "state");
        File.WriteAllText(Path.Combine(scripts, "PatternsCache.toml"), "cache");
        File.WriteAllText(Path.Combine(scripts, "logs", "OpenVersus.log"), "log");
        File.WriteAllText(Path.Combine(scripts, "SomeoneElse.toml"), "theirs");

        string? moved = Layout.MoveInto(plugin, Home, out var notes);
        Assert.Equal(New("OpenVersus_2026.10.01.01.asi"), moved);
        Assert.Equal("plugin", File.ReadAllText(moved!));
        Assert.Equal("mine", File.ReadAllText(New("OpenVersus.toml")));
        Assert.Equal("state", File.ReadAllText(New("OVSState.toml")));
        Assert.Equal("cache", File.ReadAllText(New("PatternsCache.toml")));
        Assert.False(File.Exists(plugin));
        Assert.True(File.Exists(Path.Combine(scripts, "logs", "OpenVersus.log")));
        Assert.True(File.Exists(Path.Combine(scripts, "SomeoneElse.toml")));
        Assert.Contains(notes, n => n.StartsWith("moved ") && n.Contains("OpenVersus_2026.10.01.01.asi"));
    }

    /// <summary>When the plugin cannot move, nothing does.</summary>
    [Fact]
    public void WhenThePluginCannotMoveNothingDoes()
    {
        string scripts = Path.Combine(_plugins, "scripts-blocked");
        Directory.CreateDirectory(scripts);
        string plugin = Path.Combine(scripts, "OpenVersus_2026.10.01.01.asi");
        File.WriteAllText(plugin, "plugin");
        File.WriteAllText(Path.Combine(scripts, "OpenVersus.toml"), "mine");
        File.WriteAllText(New("OpenVersus_2026.10.01.01.asi"), "already there");

        Assert.Null(Layout.MoveInto(plugin, Home, out var notes));
        Assert.True(File.Exists(plugin));
        Assert.True(File.Exists(Path.Combine(scripts, "OpenVersus.toml")));
        Assert.False(File.Exists(New("OpenVersus.toml")));
        Assert.Contains(notes, n => n.Contains("already exists"));
    }

    [Fact]
    public void FoldersCompareRegardlessOfCaseAndTrailingSeparators()
    {
        Assert.True(Layout.SameFolder(Home, Home + Path.DirectorySeparatorChar));
        Assert.True(Layout.SameFolder(Home, Path.Combine(_plugins, "openversus")));
        Assert.False(Layout.SameFolder(Home, _plugins));
    }
}
