using OpenVersus.Config;

namespace OpenVersus.Tests;

public class SettingsMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-migrate-").FullName;

    public void Dispose()
    {
        UnixPermissions.Restore(_dir);
        Directory.Delete(_dir, recursive: true);
    }

    private string TomlPath => Path.Combine(_dir, Settings.FileName);
    private string IniPath => Path.Combine(_dir, Settings.LegacyFileName);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", name);

    /// <summary>An OpenVersus.ini as the C++ client wrote it (Key=Value, no blank lines, CRLF, the
    /// five retired keys) converts line for line, loses nothing, gains the rows it lacked in its
    /// own style, and the ini is gone.</summary>
    [Fact]
    public void TheCppClientsFileConverts()
    {
        File.Copy(Fixture("cpp-client.ini"), IniPath);
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.False(File.Exists(IniPath));
        string text = File.ReadAllText(TomlPath);
        string[] lines = text.Split("\r\n");

        Assert.DoesNotContain("\r\n", text.Replace("\r\n", ""));
        Assert.Contains("ShowConsole=false", lines);
        Assert.Contains("LogLevel=\"0\"", lines);
        Assert.Contains("LogSize=\"50\"", lines);
        Assert.Contains("ModLoader=\"Kernel32.CreateFileW\"", lines);
        Assert.Contains("ToggleMenu=\"F1\"", lines);
        Assert.Contains("# This proxies only connection to the game server.", lines);
        Assert.Contains("ServerUrl=\"https://prod.openversus.org/\"", lines);
        Assert.Contains("PostMatchFreeze=true", lines);
        Assert.Contains("NetStats=false", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith(';'));
        Assert.All(lines.Where(l => l.Contains('=')), l => Assert.DoesNotContain(" =", l));

        Assert.False(s.EnableConsoleWindow);
        Assert.Equal("0", s.LogLevel);
        Assert.Null(s.Problem);
        Assert.Contains(log.Lines, l => l.Contains("Converted"));
        Assert.Equal(5, log.Lines.Count(l => l.Contains("is no longer used")));
        Assert.DoesNotContain(log.Lines, l => l.Contains("is not a setting"));

        // The converted file is the file from now on: loading again changes nothing.
        byte[] once = File.ReadAllBytes(TomlPath);
        Settings.Load(_dir);
        Assert.Equal(once, File.ReadAllBytes(TomlPath));
    }

    /// <summary>The repository's sample.ini (spaced, blank lines between sections, comments).</summary>
    [SkippableFact]
    public void TheRepositorySampleIniConverts()
    {
        string sample = SettingsTests.RepoFile("sample.ini");
        Skip.If(!File.Exists(sample), "sample.ini not found");
        File.Copy(sample, IniPath);
        string[] before = File.ReadAllLines(IniPath);

        Settings.Load(_dir);
        string[] after = File.ReadAllLines(TomlPath);
        Assert.Equal(before.Count(l => l.Length == 0), after.Count(l => l.Length == 0));
        Assert.Equal(before.Count(l => l.StartsWith(';')), after.Count(l => l.StartsWith('#')));
        Assert.Contains("LogLevel = \"0\"", after);
        Assert.Contains("PostMatchFreeze = true", after);
        Assert.False(File.Exists(IniPath));
    }

    [Fact]
    public void EveryServerUrlBecomesProd()
    {
        File.WriteAllText(IniPath, "[Server.Game]\r\nEnabled=true\r\nServerUrl=http://203.0.113.7:8000/\r\n[Server.Prod]\r\nserverurl=\"https://prod.openversus.org\"\r\n");
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ProdServerUrl);
        string[] lines = File.ReadAllLines(TomlPath);
        Assert.Equal(2, lines.Count(l => l == "ServerUrl=\"https://prod.openversus.org/\""));
        Assert.Contains(log.Lines, l => l.Contains("[Server.Game] ServerUrl was http://203.0.113.7:8000/"));
        Assert.Contains(log.Lines, l => l.Contains("[Server.Prod] ServerUrl was https://prod.openversus.org;"));
    }

    /// <summary>Everything the ini reader read is carried as it read it; what it never read stays
    /// as a comment.</summary>
    [Fact]
    public void ValuesCarryOverAsTheIniReaderReadThem()
    {
        File.WriteAllText(IniPath, """
            ; header comment ; with a second semicolon
            orphan line with no equals
            [settings.debug] ; trailing
            ShowConsole = on
            DebugLogging = 1
            DebugPause = maybe
            NonMVSPatching = TRUE
            [Settings]
            LogLevel = "debug"
            AutoUpdate = off
            AutoUpdate = on
            Weird Key = C:\games\"quoted"\path
            [Settings.Debug]
            ShowConsole = off
            [Patterns]
            SigCheck = 48 8D ? ?
            [Settings.Keybinds]
            ToggleMenu = 'F2'
            """);
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.True(s.EnableConsoleWindow);
        Assert.True(s.Debug);
        Assert.False(s.PauseOnStart);
        Assert.True(s.AllowNonMvs);
        Assert.Equal("debug", s.LogLevel);
        Assert.Equal("F2", s.MenuHotkey);
        Assert.False(s.AutoUpdate);
        Assert.Equal("48 8D ? ?", s.Pattern("SigCheck"));

        var toml = TomlConfig.Load(TomlPath);
        Assert.Empty(toml.Errors);
        Assert.Equal("maybe", toml.Get("Settings.Debug", "DebugPause"));
        Assert.Equal("C:\\games\\\"quoted\"\\path", toml.Get("Settings", "Weird Key"));

        string[] lines = File.ReadAllLines(TomlPath);
        Assert.Equal("# header comment ; with a second semicolon", lines[0]);
        Assert.Equal("# orphan line with no equals", lines[1]);
        Assert.Equal("[Settings.Debug] # trailing", lines[2]);
        Assert.Contains("ShowConsole = true", lines);
        Assert.Contains("DebugPause = \"maybe\"", lines);
        Assert.Contains("# AutoUpdate = on", lines);
        Assert.Contains("# [Settings.Debug]", lines);
        Assert.Contains("# ShowConsole = off", lines);
        Assert.Contains(log.Lines, l => l.Contains("DebugPause = \"maybe\" is not true/false"));
        Assert.Contains(log.Lines, l => l.Contains("[Settings] Weird Key is not a setting"));
        Assert.False(File.Exists(IniPath));
    }

    /// <summary>An ini whose conversion would not parse (a key and a section of the same name)
    /// gives the defaults, the log keeps the old text, and the ini is still deleted. Tomlyn 2.10.1
    /// reports this clash only when the key is the last in its table; with a key after it, the
    /// file parses and both read as the ini read them.</summary>
    [Fact]
    public void AConversionThatFailsStartsFromDefaults()
    {
        File.WriteAllText(IniPath, "[Settings]\nAutoUpdate = false\nDebug = 1\n[Settings.Debug]\nShowConsole = false\n");
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.True(s.AutoUpdate);
        Assert.True(s.EnableConsoleWindow);
        Assert.Contains(log.Lines, l => l.Contains("Could not convert") && l.Contains("AutoUpdate = false"));
        Assert.Equal("Settings reset to defaults", s.Problem?.Title);
        Assert.Empty(TomlConfig.Load(TomlPath).Errors);
        Assert.False(File.Exists(IniPath));
    }

    /// <summary>A new release extracted over an install that never converted: the zip's default
    /// TOML takes the ini's settings where it still has the default, never ServerUrl, and never
    /// over something changed in the TOML; the ini is then deleted.</summary>
    [Fact]
    public void AnIniBesideATomlIsMergedIntoItAndDeleted()
    {
        File.Copy(SettingsTests.RepoFile("dotnet/sample.toml"), TomlPath);
        string toml = File.ReadAllText(TomlPath).Replace("NetStats = false", "NetStats = true  # mine");
        File.WriteAllText(TomlPath, toml);
        File.WriteAllText(IniPath, """
            [Settings.Debug]
            ShowConsole=off
            [Settings]
            LogSize=50
            LogLevel=0
            AutoUpdate=true
            [Features]
            NetStats=false
            [Server.Game]
            ServerUrl=http://203.0.113.7:8000/
            [Custom]
            Mine=kept
            """);
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.False(s.EnableConsoleWindow);
        Assert.Equal("0", s.LogLevel);
        Assert.True(s.AutoUpdate);
        Assert.True(s.NetStats);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Assert.False(File.Exists(IniPath));

        var after = TomlConfig.Load(TomlPath);
        Assert.Empty(after.Errors);
        Assert.Equal("50", after.Get("Settings", "LogSize"));
        Assert.Equal("kept", after.Get("Custom", "Mine"));
        string[] lines = File.ReadAllLines(TomlPath);
        Assert.Contains("NetStats = true  # mine", lines);
        Assert.Contains("ShowConsole = false", lines);
        Assert.Contains(log.Lines, l => l.Contains("took 4 setting(s)"));
        Assert.Contains(log.Lines, l => l.Contains("[Settings.Debug] ShowConsole = off, from OpenVersus.ini"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("ServerUrl = http"));
    }

    [Fact]
    public void AnIniBesideABrokenTomlIsLeftAlone()
    {
        File.WriteAllText(TomlPath, "[Settings]\nAutoUpdate = = false\n");
        File.WriteAllText(IniPath, "[Settings]\nAutoUpdate = false\n");
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.True(s.AutoUpdate);
        Assert.True(File.Exists(IniPath));
        Assert.Contains(log.Lines, l => l.Contains("is left as it is until OpenVersus.toml can be read"));
    }

    [Fact]
    public void AMergeThatCannotBeSavedKeepsTheIni()
    {
        File.WriteAllText(TomlPath, "[Settings]\nAutoUpdate = true\n");
        File.WriteAllText(IniPath, "[Settings]\nAutoUpdate = false\n");
        Directory.CreateDirectory(TomlPath + ".tmp");

        var s = Settings.Load(_dir);
        Assert.False(s.AutoUpdate);
        Assert.True(File.Exists(IniPath));
    }

    /// <summary>When the TOML cannot be written, the ini is the only copy: it stays, and this run
    /// uses the converted values.</summary>
    [SkippableFact]
    public void AnUnwritableDirectoryKeepsTheIni()
    {
        UnixPermissions.SkipUnlessUnix();
        File.WriteAllText(IniPath, "[Settings]\nAutoUpdate = false\n[Server.Game]\nServerUrl = http://203.0.113.7:8000/\n");
        UnixPermissions.MakeReadOnly(_dir);
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.False(s.AutoUpdate);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Assert.True(File.Exists(IniPath));
        Assert.False(File.Exists(TomlPath));
        Assert.Contains(log.Lines, l => l.Contains("Could not write"));
    }

    /// <summary>The same with the directory writable, so only the guard keeps the ini: a directory
    /// where the TOML's temporary file would go makes that one write fail.</summary>
    [Fact]
    public void AFailedWriteKeepsTheIniEvenWhereItCouldBeDeleted()
    {
        File.WriteAllText(IniPath, "[Settings]\nAutoUpdate = false\n");
        Directory.CreateDirectory(TomlPath + ".tmp");
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.False(s.AutoUpdate);
        Assert.True(File.Exists(IniPath));
        Assert.False(File.Exists(TomlPath));
    }

    /// <summary>An ini that cannot be read is the only copy of the settings: nothing is written,
    /// nothing deleted, and this run uses the defaults.</summary>
    [SkippableFact]
    public void AnUnreadableIniIsLeftForTheNextLaunch()
    {
        UnixPermissions.SkipUnlessUnix();
        Skip.If(Environment.UserName == "root", "root reads anything");
        File.WriteAllText(IniPath, "[Settings]\nAutoUpdate = false\n");
        UnixPermissions.MakeUnreadable(IniPath);
        var log = new ListLogger();

        var s = Settings.Load(_dir, log);
        Assert.True(s.AutoUpdate);
        Assert.True(File.Exists(IniPath));
        Assert.False(File.Exists(TomlPath));
        Assert.Contains(log.Lines, l => l.Contains("tried again next launch"));
        UnixPermissions.Restore(IniPath);
    }

    /// <summary>Verify compares against the ini reader, not the converter: a converted text that
    /// dropped or changed a key is caught.</summary>
    [Fact]
    public void VerifyCatchesAConversionThatLosesOrChangesAKey()
    {
        File.WriteAllText(IniPath, "[Settings]\nAutoUpdate = off\nLogLevel = 3\n");
        var ini = IniFile.Load(IniPath);

        Assert.Null(SettingsMigration.Verify(ini, SettingsMigration.Convert(ini, null)));
        Assert.Contains("LogLevel", SettingsMigration.Verify(ini, "[Settings]\nAutoUpdate = false\n"));
        Assert.Contains("AutoUpdate", SettingsMigration.Verify(ini, "[Settings]\nAutoUpdate = true\nLogLevel = \"3\"\n"));
        Assert.Contains("does not parse", SettingsMigration.Verify(ini, "[Settings]\nAutoUpdate = off\n"));
    }
}
