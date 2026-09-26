using OpenVersus.Config;

namespace OpenVersus.Tests;

public class SettingsTests
{
    [Fact]
    public void IniKeysAreUniqueAndEveryRowFieldIsInTheTable()
    {
        Assert.Equal(Settings.Table.Count, Settings.Table.Select(d => d.Section + "/" + d.Key).Distinct().Count());
        var fields = typeof(Settings.Rows).GetFields().Select(f => (SettingDef)f.GetValue(null)!).ToList();
        Assert.Equal(fields.Count, Settings.Table.Count);
        Assert.All(fields, f => Assert.Contains(f, Settings.Table));
    }

    [Fact]
    public void EveryTypedPropertyReadsARowThatExists()
    {
        // The properties are the only hand-written list; each must resolve to a table row.
        var s = Settings.FromValues(new Dictionary<SettingDef, string>());
        foreach (var prop in typeof(Settings).GetProperties().Where(p => p.DeclaringType == typeof(Settings) && p.Name != "Path"))
        {
            _ = prop.GetValue(s);
        }
    }

    [Fact]
    public void DefaultsMatchTheCppClient()
    {
        var s = Settings.FromValues(new Dictionary<SettingDef, string>());
        Assert.True(s.EnableConsoleWindow);
        Assert.False(s.Debug);
        Assert.True(s.AutoUpdate);
        Assert.True(s.SunsetDate);
        Assert.True(s.DisableSignatureCheck);
        Assert.False(s.NetStats);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Assert.StartsWith("48 8D 0D ? ? ? ? E9", s.Pattern("SigCheck"));
        Assert.Throws<ArgumentException>(() => s.Pattern("NoSuchPattern"));
    }

    [Fact]
    public void BooleansIgnoreCaseAndAcceptDigits()
    {
        var s = Settings.FromValues(new Dictionary<SettingDef, string>
        {
            [Settings.Rows.DebugLogging] = "True",
            [Settings.Rows.AutoUpdate] = "tRuE",
            [Settings.Rows.Notifications] = "1",
            [Settings.Rows.Dialog] = "FALSE",
            [Settings.Rows.HookUe] = "0",
            [Settings.Rows.SunsetDate] = "yes",
            [Settings.Rows.NetStats] = "on",
            [Settings.Rows.PostMatchFreeze] = "OFF",
        });
        Assert.True(s.Debug);
        Assert.True(s.AutoUpdate);
        Assert.True(s.Notifications);
        Assert.False(s.Dialog);
        Assert.False(s.HookUe);
        Assert.True(s.SunsetDate);
        Assert.True(s.NetStats);
        Assert.False(s.PostMatchFreeze);
    }

    private static string NewDir() => Directory.CreateTempSubdirectory("ovs-settings-").FullName;

    /// <summary>A complete file is not rewritten at all.</summary>
    [Fact]
    public void ACompleteFileIsLeftByteIdentical()
    {
        var text = new System.Text.StringBuilder();
        foreach (var section in Settings.Table.GroupBy(d => d.Section))
        {
            text.Append('[').Append(section.Key).Append("]\r\n");
            foreach (var def in section)
            {
                string value = def.Kind == SettingKind.Bool ? (def.Default == "true" ? "false" : "true") : SettingsMigration.Quote(def.Default);
                text.Append(def.Key).Append('=').Append(value).Append("\r\n");
            }
        }

        string dir = NewDir();
        string path = Path.Combine(dir, Settings.FileName);
        File.WriteAllText(path, text.ToString());
        byte[] original = File.ReadAllBytes(path);
        DateTime written = File.GetLastWriteTimeUtc(path);

        var s = Settings.Load(dir);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.False(s.AutoUpdate);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>With no file at all, the one written is dotnet/sample.toml byte for byte, and it
    /// reads back as every default.</summary>
    [Fact]
    public void AFileWrittenFromNothingIsSampleToml()
    {
        string dir = NewDir();
        var s = Settings.Load(dir);
        string path = Path.Combine(dir, Settings.FileName);
        Assert.Equal(File.ReadAllBytes(RepoFile("dotnet/sample.toml")), File.ReadAllBytes(path));
        foreach (var def in Settings.Table)
        {
            Assert.Equal(def.Default, TomlConfig.Load(path).Get(def.Section, def.Key));
        }

        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Every row and table has its comment, and the commented default parses back to every
    /// default with nothing unknown in it.</summary>
    [Fact]
    public void TheDefaultFileExplainsEverySettingAndReadsBackAsTheDefaults()
    {
        Assert.All(Settings.Table, def => Assert.True(DefaultConfig.Comments.ContainsKey(def), $"no comment for {def}"));
        Assert.All(Settings.Table.Select(d => d.Section).Distinct(), section => Assert.True(DefaultConfig.Sections.ContainsKey(section), $"no banner for [{section}]"));
        Assert.Equal(Settings.Table.Count, DefaultConfig.Comments.Count);

        string dir = NewDir();
        var log = new ListLogger();
        Settings.Load(dir, log);
        var toml = TomlConfig.Load(Path.Combine(dir, Settings.FileName));
        Assert.Empty(toml.Errors);
        Assert.All(Settings.Table, def => Assert.Equal(def.Default, toml.Get(def.Section, def.Key)));
        Assert.Equal(Settings.Table.Count, toml.Keys().Count());
        Assert.DoesNotContain(log.Lines, l => l.Contains("is not a setting") || l.Contains("is not true/false"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void BadValuesAreWarnedAboutAndLeftOnDisk()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, Settings.FileName);
        File.WriteAllText(path, "[Settings]\nAutoUpdate = \"yes\"\nEnableKeyboardHotkeys = false\n");
        var log = new ListLogger();

        var s = Settings.Load(dir, log);
        Assert.True(s.AutoUpdate);
        Assert.False(s.EnableKeyboardHotkeys);
        Assert.Equal(1, log.Lines.Count(l => l.Contains("is not true/false")));
        Assert.Contains(log.Lines, l => l.Contains("AutoUpdate = \"yes\""));
        Assert.Contains("AutoUpdate = \"yes\"", File.ReadAllLines(path));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void AFileThatIsNotTomlIsLeftAloneAndEverySettingIsItsDefault()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, Settings.FileName);
        File.WriteAllText(path, "[Settings]\nAutoUpdate = false\n[Server.Game]\nServerUrl = https://my.server/\n");
        byte[] before = File.ReadAllBytes(path);
        var log = new ListLogger();

        var s = Settings.Load(dir, log);
        Assert.True(s.AutoUpdate);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains(log.Lines, l => l.Contains("is not valid TOML") && l.Contains("line 4, column 13"));
        Assert.Equal(new SettingsProblem("OpenVersus.toml has a mistake", "Line 4, column 13. Using default settings until it is fixed"), s.Problem);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void RetiredAndUnknownKeysAreReportedApart()
    {
        string dir = NewDir();
        File.WriteAllText(Path.Combine(dir, Settings.FileName), "[Settings]\nlogsize = \"50\"\nMadeUp = true\n");
        var log = new ListLogger();

        Settings.Load(dir, log);
        Assert.Contains(log.Lines, l => l.Contains("[Settings] LogSize is no longer used") && l.Contains("no size to cap"));
        Assert.Contains(log.Lines, l => l.Contains("[Settings] MadeUp is not a setting this version knows"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("LogSize is not a setting"));
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>LogLevel takes a number, bare or quoted, or a quoted name, through a real file.</summary>
    [Theory]
    [InlineData("3", Microsoft.Extensions.Logging.LogLevel.Warning)]
    [InlineData("\"3\"", Microsoft.Extensions.Logging.LogLevel.Warning)]
    [InlineData("\"debug\"", Microsoft.Extensions.Logging.LogLevel.Debug)]
    [InlineData("48549848", Microsoft.Extensions.Logging.LogLevel.None)]
    [InlineData("0", Microsoft.Extensions.Logging.LogLevel.Information)]
    public void LogLevelTakesANumberOrAName(string literal, Microsoft.Extensions.Logging.LogLevel expected)
    {
        string dir = NewDir();
        File.WriteAllText(Path.Combine(dir, Settings.FileName), $"[Settings]\nLogLevel = {literal}\n");

        var s = Settings.Load(dir);
        Assert.Null(s.Problem);
        Assert.Equal(expected, Log.ResolveLevel(s.LogLevel, s.Debug));
        Directory.Delete(dir, recursive: true);
    }

    [SkippableFact]
    public void AnUnwritableFileStillLoadsAndWarns()
    {
        UnixPermissions.SkipUnlessUnix();
        string dir = NewDir();
        File.WriteAllText(Path.Combine(dir, Settings.FileName), "[Settings]\nAutoUpdate = false\n");
        UnixPermissions.MakeReadOnly(dir);
        var log = new ListLogger();
        try
        {
            var s = Settings.Load(dir, log);
            Assert.False(s.AutoUpdate);
            Assert.True(s.SunsetDate);
            Assert.Contains(log.Lines, l => l.Contains("Could not write"));
        }
        finally
        {
            UnixPermissions.Restore(dir);
            Directory.Delete(dir, recursive: true);
        }
    }

    internal static string RepoFile(string name) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", name);
}
