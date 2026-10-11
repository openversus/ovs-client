namespace OpenVersus.Tests;

public class VersionTests
{
    /// <summary>The product version is the version, then the commit (when git is there), then
    /// the build date, as the version resource and the OpenVersusInfo export carry it.</summary>
    [Fact]
    public void TheProductVersionCarriesTheVersionCommitAndBuildDate()
    {
        Assert.StartsWith(OvsVersion.Current + "+", OvsVersion.Informational);
        string[] metadata = OvsVersion.Informational[(OvsVersion.Current.Length + 1)..].Split('.');
        Assert.Matches(@"^20\d{6}$", metadata[^1]);
    }

    /// <summary>The C# client is Christopher Conley's and Tuggernuts'; the C++ client's author is
    /// credited in LICENSE, not in this binary.</summary>
    [Fact]
    public void TheCopyrightNamesTheCSharpClientsAuthors()
    {
        Assert.Equal("Copyright (c) 2026 Christopher Conley, Tuggernuts", OvsVersion.Copyright);
    }
}
