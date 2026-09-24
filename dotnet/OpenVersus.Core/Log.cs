using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenVersus;

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
///
/// It is an <see cref="ILogger"/>, so code can take the abstraction and use the standard
/// extension methods; the level filter is <see cref="MinimumLevel"/>, set from the ini.
/// </summary>
public sealed class Log : ILogger, IDisposable
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
    /// <summary>Lines below this level are dropped before they are queued.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
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

    public void Trace(string message) => Line(LogLevel.Trace, message);
    public void Debug(string message) => Line(LogLevel.Debug, message);
    public void Info(string message) => Line(LogLevel.Information, message);
    public void Warn(string message) => Line(LogLevel.Warning, message);
    public void Error(string message) => Line(LogLevel.Error, message);
    public void Critical(string message) => Line(LogLevel.Critical, message);
    /// <summary>Information, shown green on the console: the C++ printfSuccess.</summary>
    public void Success(string message) => Line(LogLevel.Information, message, "\x1b[32m");

    // ILogger: the standard extension methods (LogDebug, LogInformation, ...) land here.
    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel && !_closed;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    void ILogger.Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        string message = formatter(state, exception);
        if (exception != null)
        {
            message += Environment.NewLine + exception;
        }

        Line(level, message);
    }

    /// <summary>
    /// The minimum level from the ini: the LogLevel key by name (trace, debug, info, warn,
    /// error, critical, none) or number (1 debug .. 6 none; 0 means not set), else Debug when
    /// DebugLogging is on and Information when it is off, which is what the two settings meant
    /// before levels existed.
    /// </summary>
    public static LogLevel ResolveLevel(string? logLevel, bool debugLogging)
    {
        string text = (logLevel ?? "").Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n != 0)
        {
            return (LogLevel)Math.Clamp(n, 1, 6);
        }

        return text.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" or "fatal" => LogLevel.Critical,
            "none" or "off" => LogLevel.None,
            _ => debugLogging ? LogLevel.Debug : LogLevel.Information,
        };
    }

    public void Line(LogLevel level, string message, string? colour = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        string stamp = DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture);
        string tag = level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "NFO",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
        string? console = null;
        if (ConsoleWriter != null)
        {
            // Same shape as the C++ console: "[TAG] [timestamp]: message".
            string tagColour = level switch
            {
                LogLevel.Trace => "\x1b[90m",
                LogLevel.Debug or LogLevel.Warning => "\x1b[33m",
                LogLevel.Information => "\x1b[32m",
                _ => "\x1b[31m",
            };
            console = Colour
                ? $"\x1b[0m[{tagColour}{tag}\x1b[0m] [{stamp}]: {colour}{message}\x1b[0m\n"
                : $"[{tag}] [{stamp}]: {message}\n";
        }
        try
        {
            _queue.Add((level, $"{stamp} [{tag}] {message}", console, null));
        }
        catch (InvalidOperationException) { }
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
                    if (_queue.IsCompleted)
                    {
                        break;
                    }

                    writer.Flush();
                    continue;
                }
                if (item.Text != null)
                {
                    writer.WriteLine(item.Text);
                    try
                    {
                        if (item.Console != null)
                        {
                            ConsoleWriter?.Invoke(item.Console);
                        }
                    }
                    catch { }
                }
                if (item.Level >= LogLevel.Warning || item.Flushed != null || _queue.Count == 0 && _queue.IsAddingCompleted)
                {
                    writer.Flush();
                }

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
        try
        {
            _queue.Add((LogLevel.Trace, null, null, flushed));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        return flushed.Wait(timeout ?? TimeSpan.FromSeconds(2));
    }

    /// <summary>Flushes, stops the writer, and for a session log makes the launch-time copy. Safe to call more than once.</summary>
    public void Close()
    {
        lock (_queue)
        {
            if (_closed)
            {
                return;
            }

            Line(LogLevel.Information, ClosedMarker);
            _closed = true;
            _queue.CompleteAdding();
        }
        _writer.Join(TimeSpan.FromSeconds(5));
        if (_name != null)
        {
            try
            {
                Archive(Path, ArchivePath(Path, _name, LaunchTime));
            }
            catch { }
        }
    }

    public void Dispose() => Close();

    private static string ArchivePath(string path, string name, DateTime launch) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{name}_{launch.ToString(ArchiveStampFormat, CultureInfo.InvariantCulture)}.log");

    private static void Archive(string path, string target)
    {
        if (!File.Exists(target))
        {
            File.Copy(path, target);
        }
    }

    /// <summary>The previous run's file, if it was never archived (its process ended without a Close).</summary>
    private static void ArchiveLeftover(string path, string name)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return;
            }

            string? first = File.ReadLines(path).FirstOrDefault();
            if (first == null || first.Length < StampFormat.Length
                || !DateTime.TryParseExact(first[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime launch))
            {
                launch = File.GetCreationTime(path);
            }

            Archive(path, ArchivePath(path, name, launch));
        }
        catch { }
    }

    /// <summary>Bytes as the log shows them everywhere: upper-case hex, no separators.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
