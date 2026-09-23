using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace OpenVersus;

public enum LogLevel { Debug, Info, Warn, Error, Critical }

/// <summary>
/// The client's log. Lines are queued and written by one background thread, so a hook never
/// waits on the disk; the writer flushes every 250 ms and at once after a warning or error, so
/// a crash loses at most a quarter second. The console mirror uses the prefixes and colours
/// the C++ client printed, with the timestamp.
///
/// A session log (<see cref="OpenSession"/>) lives in a logs directory as a fixed name,
/// truncated at launch, so the running log is always that file. At exit it is copied to
/// "&lt;name&gt;_yyyy-MM-dd-HH.mm.ss.log" named for the launch time. Since a crash or a kill gives no
/// exit moment, the next launch makes that copy first if it is missing, from the launch time on
/// the old file's first line. So the last run's log is always there under its launch time,
/// whether the game is running or not.
/// </summary>
public sealed class Log : IDisposable
{
	private const string StampFormat = "yyyy-MM-dd HH:mm:ss.fff";
	private const string ArchiveStampFormat = "yyyy-MM-dd-HH.mm.ss";
	private const string ClosedMarker = "log closed";

	private readonly BlockingCollection<(LogLevel Level, string? Text, string? Console, ManualResetEventSlim? Flushed)> _queue = new();
	private readonly Thread _writer;
	private readonly string? _name;
	private volatile bool _closed;

	public string Path { get; }
	public DateTime LaunchTime { get; } = DateTime.Now;

	/// <summary>Where console lines go once a console window exists; null means no console.</summary>
	public Action<string>? ConsoleWriter { get; set; }
	/// <summary>Whether Debug lines are written at all. The C++ DebugLogging setting.</summary>
	public bool DebugEnabled { get; set; }
	/// <summary>Colour escapes in the console mirror; the file never has them.</summary>
	public bool Colour { get; set; } = true;

	/// <summary>A log at <paramref name="path"/>, truncated now, with no archiving.</summary>
	public Log(string path) : this(path, null) { }

	private Log(string path, string? name)
	{
		Path = path;
		_name = name;
		File.WriteAllText(path, "");
		_writer = new Thread(WriteLoop) { IsBackground = true, Name = "OVS log writer" };
		_writer.Start();
	}

	/// <summary>
	/// The session log "&lt;directory&gt;/&lt;name&gt;.log". If the previous run's file is still there and
	/// was never archived, it is copied to its launch-time name first.
	/// </summary>
	public static Log OpenSession(string directory, string name)
	{
		Directory.CreateDirectory(directory);
		string path = System.IO.Path.Combine(directory, name + ".log");
		ArchiveLeftover(path, name);
		var log = new Log(path, name);
		AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Close();
		return log;
	}

	public void Debug(string message) { if (DebugEnabled) Line(LogLevel.Debug, message); }
	public void Info(string message) => Line(LogLevel.Info, message);
	public void Warn(string message) => Line(LogLevel.Warn, message);
	public void Error(string message) => Line(LogLevel.Error, message);
	public void Critical(string message) => Line(LogLevel.Critical, message);
	/// <summary>Info, shown green on the console: the C++ printfSuccess.</summary>
	public void Success(string message) => Line(LogLevel.Info, message, "\x1b[32m");

	public void Line(LogLevel level, string message, string? colour = null)
	{
		if (_closed) return;
		string stamp = DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture);
		string tag = level switch
		{
			LogLevel.Debug => "DBG",
			LogLevel.Info => "NFO",
			LogLevel.Warn => "WRN",
			LogLevel.Error => "ERR",
			_ => "CRT",
		};
		string? console = null;
		if (ConsoleWriter != null)
		{
			// Same shape as the C++ console: "[TAG] [timestamp]: message".
			string tagColour = level switch
			{
				LogLevel.Debug or LogLevel.Warn => "\x1b[33m",
				LogLevel.Info => "\x1b[32m",
				_ => "\x1b[31m",
			};
			console = Colour
				? $"\x1b[0m[{tagColour}{tag}\x1b[0m] [{stamp}]: {colour}{message}\x1b[0m\n"
				: $"[{tag}] [{stamp}]: {message}\n";
		}
		try { _queue.Add((level, $"{stamp} [{tag}] {message}", console, null)); } catch (InvalidOperationException) { }
	}

	private void WriteLoop()
	{
		try
		{
			using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 14);
			using var writer = new StreamWriter(stream, new UTF8Encoding(false));
			while (true)
			{
				if (!_queue.TryTake(out var item, 250))
				{
					if (_queue.IsCompleted) break;
					writer.Flush();
					continue;
				}
				if (item.Text != null)
				{
					writer.WriteLine(item.Text);
					try { if (item.Console != null) ConsoleWriter?.Invoke(item.Console); } catch { }
				}
				if (item.Level >= LogLevel.Warn || item.Flushed != null || _queue.Count == 0 && _queue.IsAddingCompleted)
					writer.Flush();
				item.Flushed?.Set();
			}
			writer.Flush();
		}
		catch { /* a full or read-only disk must not stop the game */ }
	}

	/// <summary>Waits until everything queued so far is on disk, up to <paramref name="timeout"/>.</summary>
	public bool Flush(TimeSpan? timeout = null)
	{
		using var flushed = new ManualResetEventSlim(false);
		try { _queue.Add((LogLevel.Debug, null, null, flushed)); } catch (InvalidOperationException) { return false; }
		return flushed.Wait(timeout ?? TimeSpan.FromSeconds(2));
	}

	/// <summary>Flushes, stops the writer, and for a session log makes the launch-time copy. Safe to call more than once.</summary>
	public void Close()
	{
		lock (_queue)
		{
			if (_closed) return;
			Line(LogLevel.Info, ClosedMarker);
			_closed = true;
			_queue.CompleteAdding();
		}
		_writer.Join(TimeSpan.FromSeconds(5));
		if (_name != null)
			try { Archive(Path, ArchivePath(Path, _name, LaunchTime)); } catch { }
	}

	public void Dispose() => Close();

	private static string ArchivePath(string path, string name, DateTime launch) =>
		System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{name}_{launch.ToString(ArchiveStampFormat, CultureInfo.InvariantCulture)}.log");

	private static void Archive(string path, string target)
	{
		if (!File.Exists(target))
			File.Copy(path, target);
	}

	/// <summary>The previous run's file, if it was never archived (its process ended without a Close).</summary>
	private static void ArchiveLeftover(string path, string name)
	{
		try
		{
			if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
			string? first = File.ReadLines(path).FirstOrDefault();
			if (first == null || first.Length < StampFormat.Length
				|| !DateTime.TryParseExact(first[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime launch))
				launch = File.GetCreationTime(path);
			Archive(path, ArchivePath(path, name, launch));
		}
		catch { }
	}

	/// <summary>Bytes as the log shows them everywhere: upper-case hex, no separators.</summary>
	public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
