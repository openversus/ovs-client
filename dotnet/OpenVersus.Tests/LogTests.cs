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
            // A "previous run" that ended without Close: a file with a stamped first line. The
            // stamp is an hour ago, so retention (a week) never touches it however old this test gets.
            Directory.CreateDirectory(dir);
            string running = Path.Combine(dir, "OpenVersus.log");
            DateTime previous = DateTime.Now.AddHours(-1);
            string previousStamp = previous.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            string previousArchive = Path.Combine(dir, $"OpenVersus_{previous:yyyy-MM-dd-HH.mm.ss}.log");
            File.WriteAllText(running, $"{previousStamp} [NFO] On Attach Initialize\n{previousStamp} [DBG] something\n");

            using (var log = Log.OpenSession(dir, "OpenVersus"))
            {
                log.Info("hello");
                Assert.True(log.Flush());
                Assert.True(File.Exists(previousArchive), "previous run not archived");
                Assert.Contains("something", File.ReadAllText(previousArchive));
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
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ArchivesOlderThanAWeekAreCompressedAndTheRestStayHot()
    {
        string dir = Directory.CreateTempSubdirectory("ovs-logtest-").FullName;
        try
        {
            var now = new DateTime(2026, 9, 24, 12, 0, 0);
            string old = Path.Combine(dir, "OpenVersus_2026-09-10-08.00.00.log");
            string edge = Path.Combine(dir, "OpenVersus_2026-09-17-11.59.59.log");
            string fresh = Path.Combine(dir, "OpenVersus_2026-09-20-08.00.00.log");
            string unnamed = Path.Combine(dir, "OpenVersus_not-a-stamp.log");
            string running = Path.Combine(dir, "OpenVersus.log");
            string body = string.Concat(Enumerable.Repeat("2026-09-10 08:00:00.000 [NFO] a line that repeats so zstd has something to do\n", 200));
            foreach (string f in new[] { old, edge, fresh, unnamed, running })
            {
                File.WriteAllText(f, body);
            }

            File.SetLastWriteTime(unnamed, now.AddDays(-30));
            File.SetLastWriteTime(old, now.AddDays(-14));

            var compressed = Log.CompressColdArchives(dir, "OpenVersus", now);

            Assert.Equal(3, compressed.Count);
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(edge));
            Assert.False(File.Exists(unnamed));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(running));
            Assert.True(File.Exists(old + ".zst"));
            Assert.True(new FileInfo(old + ".zst").Length < body.Length / 4, "not much of a compression");
            Assert.Equal(now.AddDays(-14), File.GetLastWriteTime(old + ".zst"));

            using var input = File.OpenRead(old + ".zst");
            using var zstd = new ZstdSharp.DecompressionStream(input);
            using var reader = new StreamReader(zstd);
            Assert.Equal(body, reader.ReadToEnd());

            // A second pass finds nothing left to do.
            Assert.Empty(Log.CompressColdArchives(dir, "OpenVersus", now));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ABusyLogStillReachesTheDiskEveryQuarterSecond()
    {
        string path = Path.Combine(Path.GetTempPath(), "ovs-logtest-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var log = new Log(path);
            var stop = new ManualResetEventSlim();
            // Information lines without pause: nothing here triggers a flush on its own.
            var producer = new Thread(() =>
            {
                int i = 0;
                while (!stop.IsSet)
                {
                    log.Info($"l{i++}");
                    Thread.Sleep(5);
                }
            });
            producer.Start();
            Thread.Sleep(Log.FlushIntervalMs * 3);
            long size = new FileInfo(path).Length;
            stop.Set();
            producer.Join();
            Assert.True(size > 0, "nothing reached the disk while the log was busy");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void VerbsGoThroughILoggerAndSuccessIsGreenOnTheConsole()
    {
        string path = Path.Combine(Path.GetTempPath(), "ovs-logtest-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var log = new Log(path);
            var console = new List<string>();
            log.ConsoleWriter = console.Add;
            ILogger logger = log;
            logger.Success("patched");
            logger.Info("""body {"steamId":"1"} with {braces}""");
            Assert.True(log.Flush());
            string text = File.ReadAllText(path);
            Assert.Contains("[NFO] patched", text);
            Assert.Contains("""body {"steamId":"1"} with {braces}""", text);
            Assert.Contains(console, l => l.Contains("\x1b[32mpatched"));
            Assert.DoesNotContain(console, l => l.Contains("\x1b[32mbody"));

            var list = new ListLogger();
            list.Warn("through any ILogger");
            Assert.Equal(["through any ILogger"], list.Lines);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
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
    public void LevelAliasesAndOutOfRangeValuesResolveWithANote()
    {
        Assert.Equal(LogLevel.Trace, Log.ResolveLevel("verbose", false, out string? note));
        Assert.Null(note);
        Assert.Equal(LogLevel.Trace, Log.ResolveLevel("ALL", false, out _));
        Assert.Equal(LogLevel.Error, Log.ResolveLevel("err", false, out _));
        Assert.Equal(LogLevel.None, Log.ResolveLevel("quiet", false, out _));
        Assert.Equal(LogLevel.Warning, Log.ResolveLevel("warn", false, out note));
        Assert.Null(note);

        // Past the end of the scale is the end of the scale: the intention is "as far as it goes".
        Assert.Equal(LogLevel.None, Log.ResolveLevel("48549848", false, out note));
        Assert.Contains("past the end", note);

        Assert.Equal(LogLevel.Information, Log.ResolveLevel("bogus", false, out note));
        Assert.Contains("bogus", note);
        Assert.Equal(LogLevel.Debug, Log.ResolveLevel("bogus", true, out _));
        Assert.Equal(LogLevel.Information, Log.ResolveLevel("default", false, out note));
        Assert.Null(note);
    }

    [SkippableFact]
    public void AnUnwritableLogsDirectoryFallsBackToThePluginDirectory()
    {
        UnixPermissions.SkipUnlessUnix();
        string plugin = Directory.CreateTempSubdirectory("ovs-logtest-").FullName;
        string locked = Directory.CreateTempSubdirectory("ovs-logtest-").FullName;
        UnixPermissions.MakeReadOnly(locked);
        try
        {
            using var log = Log.OpenSession(Path.Combine(locked, "logs"), "OpenVersus", fallbackDirectory: plugin);
            Assert.Null(log.FileError);
            Assert.Equal(Path.Combine(plugin, "OpenVersus.log"), log.Path);
            Assert.Contains("logging to", log.Notice);
            Assert.Contains(Path.Combine(locked, "logs"), log.Notice);
            log.Info("landed");
            Assert.True(log.Flush());
            Assert.Contains("[NFO] landed", File.ReadAllText(log.Path));
            log.Close();
            Assert.True(File.Exists(Path.Combine(plugin, $"OpenVersus_{log.LaunchTime:yyyy-MM-dd-HH.mm.ss}.log")), "fallback log not archived on Close");
        }
        finally
        {
            UnixPermissions.Restore(locked);
            Directory.Delete(locked, recursive: true);
            Directory.Delete(plugin, recursive: true);
        }
    }

    [SkippableFact]
    public void AnUnwritableDirectoryStillGivesAWorkingLog()
    {
        UnixPermissions.SkipUnlessUnix();
        string dir = Directory.CreateTempSubdirectory("ovs-logtest-").FullName;
        UnixPermissions.MakeReadOnly(dir);
        try
        {
            using var log = Log.OpenSession(Path.Combine(dir, "logs"), "OpenVersus", fallbackDirectory: Path.Combine(dir, "also-locked"));
            Assert.NotNull(log.FileError);
            Assert.Contains("console only", log.Notice);
            var lines = new List<string>();
            log.ConsoleWriter = lines.Add;
            log.Info("still here");
            log.Warn("and warning");
            Assert.True(log.Flush());
            Assert.Contains(lines, l => l.Contains("still here"));
            log.Close();
            Assert.False(Directory.Exists(Path.Combine(dir, "logs")));
        }
        finally
        {
            UnixPermissions.Restore(dir);
            Directory.Delete(dir, recursive: true);
        }
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
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
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
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
