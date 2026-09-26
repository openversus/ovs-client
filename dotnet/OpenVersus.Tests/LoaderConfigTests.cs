namespace OpenVersus.Tests;

/// <summary>Whether Ultimate ASI Loader loads plugins/OpenVersus/, read as the loader reads its settings.</summary>
public class LoaderConfigTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("ovs-loader-").FullName;

    public LoaderConfigTests() => File.WriteAllText(Path.Combine(_game, "xinput1_3.dll"), "");

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void NoSettingsMeansTheDefaultsWhichLoadSubfolders()
    {
        Assert.True(LoaderConfig.LoadsPluginSubfolders(_game, out string why));
        Assert.Contains("defaults", why);
    }

    [Fact]
    public void TheShippedGlobalIniLoadsSubfolders()
    {
        Write("scripts/global.ini", "[GlobalSets]\r\nLoadPlugins=1\r\nLoadFromScriptsOnly=0\r\nLoadRecursively=1\r\nDontLoadFromDllMain=1\r\n");
        Assert.True(LoaderConfig.LoadsPluginSubfolders(_game, out _));
    }

    [Theory]
    [InlineData("xinput1_3.ini", "[GlobalSets]\nLoadRecursively=0\n", "LoadRecursively is off")]
    [InlineData("global.ini", "[globalsets]\nloadplugins=0\n", "LoadPlugins is off")]
    [InlineData("plugins/global.ini", "[GlobalSets]\nLoadRecursively=no\n", "LoadRecursively is off")]
    [InlineData("update/global.ini", "[GlobalSets]\nLoadPlugins=-1\n", "LoadPlugins is off")]
    public void ASettingTurnedOffAnywhereStopsIt(string file, string text, string reason)
    {
        Write(file, text);
        Assert.False(LoaderConfig.LoadsPluginSubfolders(_game, out string why));
        Assert.Contains(reason, why);
        Assert.Contains(Path.GetFileName(file), why);
    }

    /// <summary>A later file overrides an earlier one, as in the loader.</summary>
    [Fact]
    public void LaterFilesWin()
    {
        Write("xinput1_3.ini", "[GlobalSets]\nLoadRecursively=0\n");
        Write("plugins/global.ini", "[GlobalSets]\nLoadRecursively=1\n");
        Assert.True(LoaderConfig.LoadsPluginSubfolders(_game, out _));

        Write("update/global.ini", "[GlobalSets]\nLoadRecursively=0\n");
        Assert.False(LoaderConfig.LoadsPluginSubfolders(_game, out _));
    }

    /// <summary>The loader may be renamed (version.dll on Deck); its .ini is named after it.</summary>
    [Fact]
    public void ARenamedLoadersIniIsRead()
    {
        File.WriteAllText(Path.Combine(_game, "version.dll"), "");
        Write("version.ini", "[GlobalSets]\nLoadPlugins=0\n");
        Assert.False(LoaderConfig.LoadsPluginSubfolders(_game, out string why));
        Assert.Contains("version.ini", why);
    }
}
