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
			_ = prop.GetValue(s);
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
	public void BooleansParseLikeTheCppReadBoolean()
	{
		var s = Settings.FromValues(new Dictionary<string, string> { ["bDebug"] = "True", ["bAutoUpdate"] = "TRUE", ["bNotifs"] = "1" });
		Assert.True(s.Debug);
		Assert.False(s.AutoUpdate);
		Assert.False(s.Notifications);
	}

	[Fact]
	public void VersionConstantMatchesTheRepoVersionFile()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "VERSION");
		Skip.If(!File.Exists(path), "VERSION file not found");
		Assert.Equal(OvsVersion.Current, File.ReadAllText(path).Trim());
	}
}
