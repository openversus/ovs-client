using System.Text;
using OpenVersus.Config;

namespace OpenVersus.Tests;

public class TomlConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-toml-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string text, string name = "a.toml")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        return path;
    }

    private const string Commented = """
        # The player's own header

        [Settings]
        NetStats   =   true    # inline, odd spacing
        ServerUrl = "https://example.org/ # not a comment"
           # indented comment inside the table

        [Settings.Debug]
        ShowConsole=false

        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AnUneditedFileIsNotWritten(string newLine)
    {
        string path = Write(Commented.ReplaceLineEndings(newLine));
        byte[] before = File.ReadAllBytes(path);

        var toml = TomlConfig.Load(path);
        Assert.Empty(toml.Errors);
        Assert.False(toml.Save());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(Encoding.UTF8.GetString(before), toml.ToString());
    }

    [Fact]
    public void ReadsValuesAsTextRegardlessOfCase()
    {
        var toml = TomlConfig.Load(Write("""
            Root = 1
            [Settings]
            Flag = true
            Number = 42
            Fraction = 1.5
            Text = "a \"quoted\" \\ value"
            Literal = 'C:\dir'
            List = [1, 2]
            Dotted.Key = 3
            [settings.debug]
            Lower = "found"
            """));

        Assert.Equal("true", toml.Get("Settings", "Flag"));
        Assert.Equal("true", toml.Get("SETTINGS", "flag"));
        Assert.Equal("42", toml.Get("Settings", "Number"));
        Assert.Equal("1.5", toml.Get("Settings", "Fraction"));
        Assert.Equal("a \"quoted\" \\ value", toml.Get("Settings", "Text"));
        Assert.Equal("C:\\dir", toml.Get("Settings", "Literal"));
        Assert.Equal("[1, 2]", toml.Get("Settings", "List"));
        Assert.Equal("found", toml.Get("Settings.Debug", "Lower"));
        Assert.Null(toml.Get("Settings", "Dotted"));
        Assert.Null(toml.Get("Settings", "Missing"));
        Assert.Null(toml.Get("Nowhere", "Flag"));
        Assert.Equal(
            [("", "Root"), ("Settings", "Flag"), ("Settings", "Number"), ("Settings", "Fraction"), ("Settings", "Text"),
             ("Settings", "Literal"), ("Settings", "List"), ("Settings", "Dotted.Key"), ("settings.debug", "Lower")],
            toml.Keys());
    }

    [Fact]
    public void InlineCommentsAndMarkersInsideStrings()
    {
        var toml = TomlConfig.Load(Write(Commented));
        Assert.Equal("true", toml.Get("Settings", "NetStats"));
        Assert.Equal("https://example.org/ # not a comment", toml.Get("Settings", "ServerUrl"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AnAddedKeyGoesUnderTheLastKeyAndNothingElseMoves(string newLine)
    {
        string path = Write(Commented.ReplaceLineEndings(newLine));
        var toml = TomlConfig.Load(path);

        toml.Add("Settings", "AutoUpdate", SettingKind.Bool, "true");
        toml.Add("Settings.Debug", "DebugPause", SettingKind.Bool, "off");
        toml.Add("Settings", "NetStats", SettingKind.Bool, "false");
        Assert.True(toml.Save());

        string expected = """
            # The player's own header

            [Settings]
            NetStats   =   true    # inline, odd spacing
            ServerUrl = "https://example.org/ # not a comment"
            AutoUpdate = true
               # indented comment inside the table

            [Settings.Debug]
            ShowConsole=false
            DebugPause=false

            """.ReplaceLineEndings(newLine);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Empty(TomlConfig.Load(path).Errors);
    }

    [Fact]
    public void AMissingTableGoesAtTheEndInTheFilesStyle()
    {
        string spaced = Write("[A]\nX = 1\n\n[B]\nY = 2\n", "spaced.toml");
        var toml = TomlConfig.Load(spaced);
        toml.Add("Patterns.MVS", "Dialog", SettingKind.Pattern, "40 ? 48");
        toml.Add("Server.Game", "Enabled", SettingKind.Bool, "true");
        Assert.True(toml.Save());
        Assert.Equal("[A]\nX = 1\n\n[B]\nY = 2\n\n[Patterns.MVS]\nDialog = \"40 ? 48\"\n\n[Server.Game]\nEnabled = true\n", File.ReadAllText(spaced));
        Assert.Equal("40 ? 48", TomlConfig.Load(spaced).Get("Patterns.MVS", "Dialog"));

        string packed = Write("[A]\r\nX=1\r\n[B]\r\nY=2\r\n", "packed.toml");
        toml = TomlConfig.Load(packed);
        toml.Add("C", "Z", SettingKind.String, "three");
        Assert.True(toml.Save());
        Assert.Equal("[A]\r\nX=1\r\n[B]\r\nY=2\r\n[C]\r\nZ=\"three\"\r\n", File.ReadAllText(packed));
    }

    [Fact]
    public void AFileWithoutAFinalLineEndingKeepsItThatWay()
    {
        string path = Write("[A]\nX = 1");
        var toml = TomlConfig.Load(path);
        toml.Add("A", "Y", SettingKind.Int, "2");
        toml.Add("B", "Z", SettingKind.Bool, "1");
        Assert.True(toml.Save());
        Assert.Equal("[A]\nX = 1\nY = 2\n\n[B]\nZ = true", File.ReadAllText(path));
    }

    [Fact]
    public void AFileWrittenFromNothingIsCrlfAndSpaced()
    {
        string path = Path.Combine(_dir, "new.toml");
        var toml = TomlConfig.Load(path);
        toml.Add("First", "A", SettingKind.Bool, "true");
        toml.Add("First", "B", SettingKind.String, "C:\\path \"with\" quotes");
        toml.Add("Second.Sub", "C", SettingKind.Int, "7");
        Assert.True(toml.Save());
        Assert.Equal("[First]\r\nA = true\r\nB = \"C:\\\\path \\\"with\\\" quotes\"\r\n\r\n[Second.Sub]\r\nC = 7\r\n", File.ReadAllText(path));
        Assert.Equal("C:\\path \"with\" quotes", TomlConfig.Load(path).Get("First", "B"));
    }

    [Fact]
    public void AnInvalidFileReadsAsEmptyAndIsNeverWritten()
    {
        string path = Write("[Settings]\nNetStats = true\nServerUrl = https://my.server/\n");
        byte[] before = File.ReadAllBytes(path);

        var toml = TomlConfig.Load(path);
        Assert.NotEmpty(toml.Errors);
        Assert.StartsWith("line 3, column 13: ", toml.Errors[0]);
        Assert.Null(toml.Get("Settings", "NetStats"));
        Assert.Empty(toml.Keys());
        toml.Add("Settings", "AutoUpdate", SettingKind.Bool, "true");
        Assert.False(toml.Save());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ATextThatIsNotUtf8IsAnError()
    {
        string path = Path.Combine(_dir, "latin1.toml");
        File.WriteAllBytes(path, [.. "[A]\nX = \""u8, 0xE9, .. "\"\n"u8]);
        var toml = TomlConfig.Load(path);
        Assert.Equal(["the file is not UTF-8 text"], toml.Errors);
        Assert.False(toml.Save());
    }

    [Fact]
    public void AByteOrderMarkIsKept()
    {
        string path = Path.Combine(_dir, "bom.toml");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "[A]\nX = 1\n"u8]);
        var toml = TomlConfig.Load(path);
        Assert.Equal("1", toml.Get("A", "X"));
        toml.Add("A", "Y", SettingKind.Int, "2");
        Assert.True(toml.Save());
        Assert.Equal([0xEF, 0xBB, 0xBF, .. "[A]\nX = 1\nY = 2\n"u8], File.ReadAllBytes(path));
    }

    [Fact]
    public void SetChangesOnlyTheValueAndKeepsItsComment()
    {
        string path = Write("[A]\nX   =   1    # count\nY = \"old\"\n");
        var toml = TomlConfig.Load(path);
        toml.Set("A", "X", SettingKind.Int, "1");
        Assert.False(toml.Save());

        toml.Set("A", "X", SettingKind.Int, "25");
        toml.Set("a", "y", SettingKind.String, "new \"one\"");
        toml.Set("A", "Z", SettingKind.Bool, "true");
        Assert.True(toml.Save());
        Assert.Equal("[A]\nX   =   25    # count\nY = \"new \\\"one\\\"\"\nZ = true\n", File.ReadAllText(path));
    }

    [Fact]
    public void KeysAboveTheFirstTableAndQuotedKeys()
    {
        string path = Path.Combine(_dir, "root.toml");
        var toml = TomlConfig.Load(path);
        toml.Set("", "Exe", SettingKind.String, "ABCD1234");
        toml.Set("Patterns", "48 8D ? ? E9", SettingKind.Int, "4096");
        toml.Set("", "Client", SettingKind.String, "2026.09.24.12");
        toml.Set("Patterns", "4C 8B ?", SettingKind.Int, "0");
        Assert.True(toml.Save());
        Assert.Equal("Exe = \"ABCD1234\"\r\nClient = \"2026.09.24.12\"\r\n\r\n[Patterns]\r\n\"48 8D ? ? E9\" = 4096\r\n\"4C 8B ?\" = 0\r\n", File.ReadAllText(path));

        toml = TomlConfig.Load(path);
        Assert.Equal("ABCD1234", toml.Get("", "Exe"));
        Assert.Equal("4096", toml.Get("Patterns", "48 8D ? ? E9"));
        toml.Set("Patterns", "48 8D ? ? E9", SettingKind.Int, "8192");
        Assert.True(toml.Save());
        Assert.Equal("8192", TomlConfig.Load(path).Get("Patterns", "48 8D ? ? E9"));
    }
}
