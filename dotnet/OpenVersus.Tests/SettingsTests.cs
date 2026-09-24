using Microsoft.Extensions.Logging;
using OpenVersus.Config;

namespace OpenVersus.Tests;

public class SettingsTests
{
    [Fact]
    public void RowNamesAndIniKeysAreUnique()
    {
        Assert.Equal(Settings.Table.Count, Settings.Table.Select(d => d.Name).Distinct().Count());
        Assert.Equal(Settings.Table.Count, Settings.Table.Select(d => d.Section + "/" + d.Key).Distinct().Count());
    }

    [Fact]
    public void EveryTypedPropertyReadsARowThatExists()
    {
        // The properties are the only hand-written list; each must resolve to a table row.
        var s = Settings.FromValues(new Dictionary<string, string>());
        foreach (var prop in typeof(Settings).GetProperties().Where(p => p.DeclaringType == typeof(Settings) && p.Name != "Path"))
        {
            _ = prop.GetValue(s);
        }
    }

    [Fact]
    public void DefaultsMatchTheCppClient()
    {
        var s = Settings.FromValues(new Dictionary<string, string>());
        Assert.True(s.EnableConsoleWindow);
        Assert.False(s.Debug);
        Assert.True(s.AutoUpdate);
        Assert.True(s.SunsetDate);
        Assert.True(s.DisableSignatureCheck);
        Assert.False(s.NetStats);
        Assert.Equal(OvsVersion.DefaultServerUrl, s.ServerUrl);
        Assert.StartsWith("48 8D 0D ? ? ? ? E9", s.Pattern("pSigCheck"));
    }

    [Fact]
    public void BooleansIgnoreCaseAndAcceptDigits()
    {
        var s = Settings.FromValues(new Dictionary<string, string>
        {
            ["bDebug"] = "True",
            ["bAutoUpdate"] = "tRuE",
            ["bNotifs"] = "1",
            ["bDialog"] = "FALSE",
            ["bHookUE"] = "0",
            ["bSunsetDate"] = "yes",
            ["bNetStats"] = "on",
            ["bPostMatchFreeze"] = "OFF",
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

    [Fact]
    public void VersionConstantMatchesTheRepoVersionFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "VERSION");
        Skip.If(!File.Exists(path), "VERSION file not found");
        Assert.Equal(OvsVersion.Current, File.ReadAllText(path).Trim());
    }

    private static string RepoFile(string name) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", name);

    private static string TempCopy(string source)
    {
        string dir = Directory.CreateTempSubdirectory("ovs-settings-").FullName;
        string path = Path.Combine(dir, Settings.FileName);
        File.Copy(source, path);
        return path;
    }

    /// <summary>Loading sample.ini adds only the rows it lacks, each at the end of its section in
    /// the file's "Key = Value" style, and leaves every existing line, blank line and comment alone.</summary>
    [SkippableFact]
    public void LoadingSampleIniOnlyAddsTheMissingRows()
    {
        string sample = RepoFile("sample.ini");
        Skip.If(!File.Exists(sample), "sample.ini not found");
        string path = TempCopy(sample);
        string[] before = File.ReadAllLines(path);

        var s = Settings.Load(path);
        string[] after = File.ReadAllLines(path);

        var added = new List<string>();
        int b = 0;
        foreach (string line in after)
        {
            if (b < before.Length && line == before[b])
            {
                b++;
            }
            else
            {
                added.Add(line);
            }
        }

        Assert.Equal(before.Length, b);
        Assert.NotEmpty(added);
        Assert.All(added, line => Assert.Matches(@"^\w+ = ", line));
        Assert.Contains("PostMatchFreeze = true", added);
        Assert.Contains("NetStats = false", added);
        Assert.Equal(after.Count(l => l.Length == 0), before.Count(l => l.Length == 0));
        Assert.Equal(after.Count(l => l.StartsWith(';')), before.Count(l => l.StartsWith(';')));
        Assert.Equal("https://prod.openversus.org/", s.ServerUrl);

        byte[] once = File.ReadAllBytes(path);
        Settings.Load(path);
        Assert.Equal(once, File.ReadAllBytes(path));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    /// <summary>A complete file in the shape the C++ client (through Wine) wrote is not rewritten at all.</summary>
    [Fact]
    public void ACompleteWineWrittenFileIsLeftByteIdentical()
    {
        var text = new System.Text.StringBuilder();
        foreach (var section in Settings.Table.GroupBy(d => d.Section))
        {
            text.Append('[').Append(section.Key).Append("]\r\n");
            foreach (var def in section)
            {
                text.Append(def.Key).Append('=').Append(def.Default == "true" ? "false" : def.Default).Append("\r\n");
            }
        }

        string dir = Directory.CreateTempSubdirectory("ovs-settings-").FullName;
        string path = Path.Combine(dir, Settings.FileName);
        File.WriteAllText(path, text.ToString());
        byte[] original = File.ReadAllBytes(path);
        DateTime written = File.GetLastWriteTimeUtc(path);

        var s = Settings.Load(path);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.False(s.AutoUpdate);
        Assert.Equal(50UL, s.Int("iLogSize"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void BadValuesAreWarnedAboutAndLeftOnDisk()
    {
        string dir = Directory.CreateTempSubdirectory("ovs-settings-").FullName;
        string path = Path.Combine(dir, Settings.FileName);
        File.WriteAllText(path, "[Settings]\nAutoUpdate = yes\nLogSize = lots\nEnableKeyboardHotkeys = FALSE\n");
        var log = new ListLogger();

        var s = Settings.Load(path, log);
        Assert.True(s.AutoUpdate);
        Assert.False(s.EnableKeyboardHotkeys);
        Assert.Equal(50UL, s.Int("iLogSize"));
        Assert.Equal(2, log.Lines.Count(l => l.Contains("is not")));
        Assert.Contains(log.Lines, l => l.Contains("AutoUpdate = \"yes\""));
        Assert.Contains(log.Lines, l => l.Contains("LogSize = \"lots\""));
        string[] after = File.ReadAllLines(path);
        Assert.Contains("AutoUpdate = yes", after);
        Assert.Contains("LogSize = lots", after);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void AnUnwritableIniStillLoadsAndWarns()
    {
        string dir = Directory.CreateTempSubdirectory("ovs-settings-").FullName;
        string path = Path.Combine(dir, Settings.FileName);
        File.WriteAllText(path, "[Settings]\nAutoUpdate = false\n");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var log = new ListLogger();
        try
        {
            var s = Settings.Load(path, log);
            Assert.False(s.AutoUpdate);
            Assert.True(s.SunsetDate);
            Assert.Contains(log.Lines, l => l.Contains("Could not write"));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
