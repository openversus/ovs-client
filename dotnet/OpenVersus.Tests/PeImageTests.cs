using OpenVersus.Memory;

namespace OpenVersus.Tests;

public class PeImageTests
{
	/// <summary>The host the Wine harness builds, if run.sh has been run; otherwise skipped.</summary>
	private static string? HostExe
	{
		get
		{
			string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "local", "wine-host", "host.exe");
			return File.Exists(path) ? Path.GetFullPath(path) : null;
		}
	}

	[SkippableFact]
	public void SectionsOfTheHarnessHost()
	{
		Skip.If(HostExe == null, "local/wine-host/host.exe not built; run dotnet/wine-host/run.sh");
		byte[] file = File.ReadAllBytes(HostExe!);
		var sections = PeImage.Sections(file);
		Assert.Contains(sections, s => s.Name == ".text" && s.IsExecutable());
		Assert.True(PeImage.SizeOfImage(file) > 0);
	}

	[Fact]
	public void NotAPeIsAnError()
	{
		Assert.Throws<FormatException>(() => PeImage.Sections(new byte[0x100]));
	}
}
