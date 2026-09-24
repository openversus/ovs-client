using System.Text;
using OpenVersus.Config;

namespace OpenVersus.Tests;

public class IniFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-ini-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text, Encoding? encoding = null, byte[]? preamble = null)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [.. preamble ?? [], .. (encoding ?? new UTF8Encoding(false)).GetBytes(text)]);
        return path;
    }

    [Fact]
    public void ReadsLikeTheProfileApi()
    {
        var ini = IniFile.Load(Write("a.ini", """
            ; leading comment
            [Main]
            Key = value
            Spaced   =   with spaces   
            Quoted = "quoted value"
            Single = 'single'
            Empty =
            Dup = first
            Dup = second
            Yes = TRUE
            One = 1
            No = fAlSe
            Zero = 0
            Junk = yes
            Lit = On
            Dark = off
            [Other]
            Key = other
            """));
        Assert.Equal("value", ini.Get("main", "KEY"));
        Assert.Equal("with spaces", ini.Get("Main", "Spaced"));
        Assert.Equal("quoted value", ini.Get("Main", "Quoted"));
        Assert.Equal("single", ini.Get("Main", "Single"));
        Assert.Equal("", ini.Get("Main", "Empty"));
        Assert.Equal("first", ini.Get("Main", "Dup"));
        Assert.Equal("other", ini.Get("Other", "Key"));
        Assert.Null(ini.Get("Main", "Missing"));
        Assert.Null(ini.Get("Missing", "Key"));
        Assert.Equal("dflt", ini.Get("Missing", "Key", "dflt"));
        Assert.True(ini.GetBool("Main", "Missing", true));
        Assert.True(ini.GetBool("Main", "Yes", false));
        Assert.True(ini.GetBool("Main", "One", false));
        Assert.False(ini.GetBool("Main", "No", true));
        Assert.False(ini.GetBool("Main", "Zero", true));
        Assert.True(ini.GetBool("Main", "Junk", true));
        Assert.True(ini.GetBool("Main", "Lit", false));
        Assert.False(ini.GetBool("Main", "Dark", true));
        Assert.Equal(7UL, ini.GetUInt64("Main", "Missing", 7));
    }

    [Fact]
    public void SettingAnExistingValueChangesOnlyTheValue()
    {
        string path = Write("a.ini", "[Main]\r\nKey  =  old   \r\n; c\r\n");
        var ini = IniFile.Load(path);
        ini.Set("main", "key", "new");
        Assert.True(ini.Save());
        Assert.Equal("[Main]\r\nKey  =  new\r\n; c\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void SavingWithoutChangesLeavesTheFileAlone()
    {
        byte[] original = "﻿[A]\r\nK=1\r\n\r\n[B]\r\nJ = \"two\"\r\n"u8.ToArray();
        string path = Path.Combine(_dir, "a.ini");
        File.WriteAllBytes(path, original);
        var ini = IniFile.Load(path);
        ini.Set("A", "K", "1");
        ini.Set("B", "J", "two");
        Assert.False(ini.Save());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingKeyGoesAtTheEndOfItsSectionInTheFilesStyle()
    {
        string path = Write("a.ini", "[A]\nOne = 1\n\n[B]\n; about two\nTwo = 2\n\n[C]\nThree = 3\n");
        var ini = IniFile.Load(path);
        ini.Set("B", "New", "n");
        ini.Set("A", "Also", "a");
        ini.Save();
        Assert.Equal("[A]\nOne = 1\nAlso = a\n\n[B]\n; about two\nTwo = 2\nNew = n\n\n[C]\nThree = 3\n", File.ReadAllText(path));
    }

    [Fact]
    public void WineWrittenFileGetsWineStyleKeysAndNoBlankLines()
    {
        string path = Write("a.ini", "[A]\r\nOne=1\r\n[B]\r\nTwo=2\r\n");
        var ini = IniFile.Load(path);
        ini.Set("A", "New", "n");
        ini.Set("C", "Three", "3");
        ini.Save();
        Assert.Equal("[A]\r\nOne=1\r\nNew=n\r\n[B]\r\nTwo=2\r\n[C]\r\nThree=3\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void MissingSectionIsAppendedWithTheFilesSeparator()
    {
        string path = Write("a.ini", "[A]\nOne = 1\n\n[B]\nTwo = 2\n");
        var ini = IniFile.Load(path);
        ini.Set("C", "Three", "3");
        ini.Save();
        Assert.Equal("[A]\nOne = 1\n\n[B]\nTwo = 2\n\n[C]\nThree = 3\n", File.ReadAllText(path));
    }

    [Fact]
    public void NewFileComesOutInTheShapeOfSampleIni()
    {
        string path = Path.Combine(_dir, "new.ini");
        var ini = IniFile.Load(path);
        ini.Set("Settings.Debug", "ShowConsole", "true");
        ini.Set("Settings.Debug", "DebugPause", "false");
        ini.Set("Settings", "LogSize", "50");
        Assert.True(ini.Save());
        Assert.Equal("[Settings.Debug]\r\nShowConsole = true\r\nDebugPause = false\r\n\r\n[Settings]\r\nLogSize = 50\r\n", File.ReadAllText(path));
        Assert.Equal("50", IniFile.Load(path).Get("Settings", "LogSize"));
    }

    [Fact]
    public void FileWithoutFinalNewLineStaysThatWay()
    {
        string path = Write("a.ini", "[A]\nOne = 1");
        var ini = IniFile.Load(path);
        ini.Set("A", "Two", "2");
        ini.Save();
        Assert.Equal("[A]\nOne = 1\nTwo = 2", File.ReadAllText(path));
    }

    [Fact]
    public void KeysMayContainSpacesAndQuestionMarks()
    {
        const string pattern = "48 8D 0D ? ? ? ? E9 ? ? ? ? CC";
        string path = Write("cache", "[3957B487.2026.04.08.14]\r\n" + pattern + "=39635228\r\n");
        var ini = IniFile.Load(path);
        Assert.Equal(39635228UL, ini.GetUInt64("3957B487.2026.04.08.14", pattern, 0));
        ini.Set("3957B487.2026.04.08.14", "FF 50 ? F3", "1");
        ini.Save();
        Assert.Equal("[3957B487.2026.04.08.14]\r\n" + pattern + "=39635228\r\nFF 50 ? F3=1\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Latin1FileRoundTripsItsBytes()
    {
        byte[] original = [.. "[A]\r\n; caf"u8, 0xE9, .. "\r\nK=1\r\n"u8];
        string path = Path.Combine(_dir, "latin1.ini");
        File.WriteAllBytes(path, original);
        var ini = IniFile.Load(path);
        ini.Set("A", "J", "2");
        ini.Save();
        Assert.Equal([.. original, .. "J=2\r\n"u8], File.ReadAllBytes(path));
    }

    [Fact]
    public void Utf16FileKeepsItsEncoding()
    {
        string path = Write("u16.ini", "[A]\r\nK=1\r\n", Encoding.Unicode, [0xFF, 0xFE]);
        var ini = IniFile.Load(path);
        Assert.Equal("1", ini.Get("A", "K"));
        ini.Set("A", "J", "2");
        ini.Save();
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("[A]\r\nK=1\r\nJ=2\r\n", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public void EmptyFileIsTreatedAsNew()
    {
        string path = Write("empty.ini", "");
        var ini = IniFile.Load(path);
        ini.Set("A", "K", "1");
        ini.Set("B", "J", "2");
        ini.Save();
        Assert.Equal("[A]\r\nK = 1\r\n\r\n[B]\r\nJ = 2\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void UnwritableFileIsReportedNotThrown()
    {
        string path = Write("ro.ini", "[A]\nK = 1\n");
        var ini = IniFile.Load(path);
        ini.Set("A", "J", "2");
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(ini.Save());
            Assert.NotNull(ini.SaveError);
        }
        finally
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal("[A]\nK = 1\n", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(ini.Save());
        Assert.Null(ini.SaveError);
        Assert.Equal("[A]\nK = 1\nJ = 2\n", File.ReadAllText(path));
    }

    [Fact]
    public void UnreadableFileLoadsEmptyAndSaysWhy()
    {
        string path = Write("locked.ini", "[A]\nK = 1\n");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var ini = IniFile.Load(path);
            Assert.NotNull(ini.LoadError);
            Assert.Null(ini.Get("A", "K"));
            Assert.Equal("d", ini.Get("A", "K", "d"));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
