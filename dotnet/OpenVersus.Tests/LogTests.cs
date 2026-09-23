using Microsoft.Extensions.Logging;

namespace OpenVersus.Tests;

public class LogTests
{
	[Fact]
	public void SessionLogIsTruncatedAndThePreviousRunIsArchivedUnderItsLaunchTime()
	{
		string dir = Path.Combine(Path.GetTempPath(), "ovs-logtest-" + Guid.NewGuid().ToString("N"));
		try
		{
			// A "previous run" that ended without Close: a file with a stamped first line.
			Directory.CreateDirectory(dir);
			string running = Path.Combine(dir, "OpenVersus.log");
			File.WriteAllText(running, "2026-09-23 16:26:28.846 [NFO] On Attach Initialize\n2026-09-23 16:26:29.001 [DBG] something\n");

			using (var log = Log.OpenSession(dir, "OpenVersus"))
			{
				log.Info("hello");
				Assert.True(log.Flush());
				Assert.True(File.Exists(Path.Combine(dir, "OpenVersus_2026-09-23-16.26.28.log")), "previous run not archived");
				Assert.Contains("something", File.ReadAllText(Path.Combine(dir, "OpenVersus_2026-09-23-16.26.28.log")));
				string text = File.ReadAllText(running);
				Assert.Contains("[NFO] hello", text);
				Assert.DoesNotContain("something", text);
				log.Close();
				Assert.Contains("log closed", File.ReadAllText(running));
				string archive = Path.Combine(dir, $"OpenVersus_{log.LaunchTime:yyyy-MM-dd-HH.mm.ss}.log");
				Assert.True(File.Exists(archive), "this run not archived on Close");
				Assert.Equal(File.ReadAllText(running), File.ReadAllText(archive));
			}
		}
		finally { try { Directory.Delete(dir, true); } catch { } }
	}

	[Fact]
	public void LevelsResolveFromTheIniAndFallBackToDebugLogging()
	{
		Assert.Equal(LogLevel.Information, Log.ResolveLevel("0", false));
		Assert.Equal(LogLevel.Debug, Log.ResolveLevel("0", true));
		Assert.Equal(LogLevel.Debug, Log.ResolveLevel("", true));
		Assert.Equal(LogLevel.Trace, Log.ResolveLevel("trace", false));
		Assert.Equal(LogLevel.Warning, Log.ResolveLevel("Warn", true));
		Assert.Equal(LogLevel.Error, Log.ResolveLevel("4", true));
		Assert.Equal(LogLevel.None, Log.ResolveLevel("none", true));
		Assert.Equal(LogLevel.None, Log.ResolveLevel("99", true));
	}

	[Fact]
	public void MinimumLevelFiltersBothTheOwnMethodsAndILogger()
	{
		string path = Path.Combine(Path.GetTempPath(), "ovs-logtest-" + Guid.NewGuid().ToString("N") + ".log");
		try
		{
			using var log = new Log(path) { MinimumLevel = LogLevel.Warning };
			ILogger logger = log;
			log.Debug("dropped");
			log.Info("dropped too");
			logger.LogInformation("dropped via ILogger {N}", 1);
			log.Warn("kept");
			logger.LogError("kept via ILogger {N}", 2);
			Assert.True(log.Flush());
			string text = File.ReadAllText(path);
			Assert.DoesNotContain("dropped", text);
			Assert.Contains("[WRN] kept", text);
			Assert.Contains("[ERR] kept via ILogger 2", text);
			Assert.False(logger.IsEnabled(LogLevel.Information));
			Assert.True(logger.IsEnabled(LogLevel.Critical));
		}
		finally { try { File.Delete(path); } catch { } }
	}

	[Fact]
	public void LinesAreStampedAndWarningsFlushPromptly()
	{
		string path = Path.Combine(Path.GetTempPath(), "ovs-logtest-" + Guid.NewGuid().ToString("N") + ".log");
		try
		{
			using var log = new Log(path);
			log.Warn("careful");
			Thread.Sleep(100);
			string text = File.ReadAllText(path);
			Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[WRN\] careful", text);
		}
		finally { try { File.Delete(path); } catch { } }
	}
}
