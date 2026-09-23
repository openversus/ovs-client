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

	private static string GameExe => Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe");

	[SkippableFact]
	public void PdataFindsTheSunsetFunctionInTheFinalBuild()
	{
		Skip.If(!File.Exists(GameExe), "game exe not installed here");
		byte[] mapped = PeImage.MapFile(File.ReadAllBytes(GameExe));
		// The SunsetDate pattern sits inside the function the toolkit's function_at reports.
		var fn = PeImage.FunctionContaining(mapped, 0x29D7535);
		Assert.Equal(((uint)0x29D7480, (uint)0x29D7583), fn);
		Assert.Null(PeImage.FunctionContaining(mapped, 0x10));
	}

	[Fact]
	public void NotAPeIsAnError()
	{
		Assert.Throws<FormatException>(() => PeImage.Sections(new byte[0x100]));
	}
}
