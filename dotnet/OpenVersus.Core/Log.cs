using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenVersus;

/// <summary>
/// The client's log. Lines are queued and written by one background thread, so a hook never
/// waits on the disk; the writer flushes every 250 ms and at once after a warning or error, so
/// a crash loses at most a quarter second. The console mirror uses the prefixes and colors
/// the C++ client printed, with the timestamp.
///
/// A session log (<see cref="OpenSession"/>) lives in a logs directory as a fixed name,
/// truncated at launch, so the running log is always that file. At exit it is copied to
/// "&lt;name&gt;_yyyy-MM-dd-HH.mm.ss.log" named for the launch time. Since a crash or a kill gives no
/// exit moment, the next launch makes that copy first if it is missing, from the launch time on
/// the old file's first line. So the last run's log is always there under its launch time,
/// whether the game is running or not.
///
/// It is an <see cref="ILogger"/>, which is what the rest of the client takes; the short verbs
/// (<c>Info</c>, <c>Warn</c>, <c>Success</c>...) are extension methods in <see cref="LogExtensions"/>,
/// and the level filter is <see cref="MinimumLevel"/>, set from the ini.
/// </summary>
public sealed class Log : ILogger, IDisposable
{
    private const string StampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const string ArchiveStampFormat = "yyyy-MM-dd-HH.mm.ss";
    private const string ClosedMarker = "log closed";
    /// <summary>The most a crash can lose: lines are flushed at least this often while they keep coming.</summary>
    public const int FlushIntervalMs = 250;
    /// <summary>Archives younger than this stay as plain text; older ones are compressed.</summary>
    public static readonly TimeSpan HotArchiveAge = TimeSpan.FromDays(7);
    /// <summary>zstd level for the cold archives: small files, and it runs once per launch in the background.</summary>
    public const int ArchiveCompressionLevel = 11;

    private readonly BlockingCollection<(LogLevel Level, string? Text, string? Console, ManualResetEventSlim? Flushed)> _queue = new();
    private readonly Thread _writer;
    private readonly string? _name;
    private volatile bool _closed;

    public string Path { get; }
    public DateTime LaunchTime { get; } = DateTime.Now;

    /// <summary>Why the file at <see cref="Path"/> could not be created or written, in which case
    /// lines still reach the console and the client still runs. Null while the file is fine.</summary>
    public Exception? FileError { get; private set; }

    /// <summary>Set when the log is not where it was asked to be: which directory failed, why, and where it went instead.</summary>
    public string? Notice { get; private init; }

    /// <summary>Appended to the archive name after the launch time ("&lt;name&gt;_&lt;time&gt;&lt;suffix&gt;.log"), for what the log was about.</summary>
    public string ArchiveSuffix { get; set; } = "";

    /// <summary>Where console lines go once a console window exists; null means no console.</summary>
    public Action<string>? ConsoleWriter { get; set; }
    /// <summary>Lines below this level are dropped before they are queued.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    /// <summary>Color escapes in the console mirror; the file never has them.</summary>
    public bool Color { get; set; } = true;

    /// <summary>A log at <paramref name="path"/>, truncated now, with no archiving.</summary>
    public Log(string path) : this(path, null, Truncate(path)) { }

    private Log(string path, string? name, Exception? fileError)
    {
        Path = path;
        _name = name;
        FileError = fileError;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "OVS log writer" };
        _writer.Start();
    }

    /// <summary>
    /// The session log "&lt;directory&gt;/&lt;name&gt;.log". If the previous run's file is still there and
    /// was never archived, it is copied to its launch-time name first. A directory that cannot be
    /// created or written is given up for <paramref name="fallbackDirectory"/>, and if that fails
    /// too the log has <see cref="FileError"/> set and lines reach the console only; either way
    /// there is a log and the client runs. <see cref="Notice"/> says what happened.
    /// </summary>
    public static Log OpenSession(string directory, string name, string? fallbackDirectory = null)
    {
        string path = System.IO.Path.Combine(directory, name + ".log");
        Exception? error = PrepareSession(directory, path, name);
        string? notice = null;
        if (error != null && fallbackDirectory != null)
        {
            string fallback = System.IO.Path.Combine(fallbackDirectory, name + ".log");
            Exception? fallbackError = PrepareSession(fallbackDirectory, fallback, name);
            if (fallbackError == null)
            {
                notice = $"log directory {directory} cannot be written ({error.Message}); logging to {fallback} instead";
                path = fallback;
                error = null;
            }
            else
            {
                notice = $"neither {directory} ({error.Message}) nor {fallbackDirectory} ({fallbackError.Message}) can hold the log; logging to the console only";
            }
        }

        var log = new Log(path, name, error) { Notice = notice };
        if (error == null)
        {
            // Off the launch path and below it in priority; nothing it can hit may reach the
            // game, since an unhandled exception on any thread is a fail-fast under NativeAOT.
            string archiveDirectory = System.IO.Path.GetDirectoryName(path)!;
            new Thread(() =>
            {
                try
                {
                    CompressColdArchives(archiveDirectory, name, DateTime.Now);
                }
                catch (Exception e)
                {
                    log.Warn($"log retention stopped: {e.Message}");
                }
            })
            { IsBackground = true, Name = "OVS log retention", Priority = ThreadPriority.BelowNormal }.Start();
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Close();
        return log;
    }

    // ILogger: the client's own verbs (LogExtensions) and the standard ones (LogInformation, ...) land here.
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

        Line(level, message, eventId.Id == LogEvents.Success.Id ? "\x1b[32m" : null);
    }

    /// <summary>
    /// The minimum level from the ini: the LogLevel key by name (trace, debug, info, warn,
    /// error, critical, none, plus the usual aliases such as verbose and all) or number (1 debug
    /// .. 6 none; 0 means not set), else Debug when DebugLogging is on and Information when it is
    /// off, which is what the two settings meant before levels existed. A number past the end of
    /// the scale means the end of the scale.
    /// </summary>
    public static LogLevel ResolveLevel(string? logLevel, bool debugLogging) => ResolveLevel(logLevel, debugLogging, out _);

    /// <summary>As above; <paramref name="note"/> says when the value was not taken literally.</summary>
    public static LogLevel ResolveLevel(string? logLevel, bool debugLogging, out string? note)
    {
        note = null;
        string text = (logLevel ?? "").Trim();
        LogLevel fallback = debugLogging ? LogLevel.Debug : LogLevel.Information;
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n != 0)
        {
            var level = (LogLevel)Math.Clamp(n, 1, 6);
            if (n > 6)
            {
                note = $"LogLevel {text} is past the end of the scale; using {(int)level} ({level})";
            }

            return level;
        }

        switch (text.ToLowerInvariant())
        {
            case "trace" or "verbose" or "all" or "v":
                return LogLevel.Trace;
            case "debug" or "dbg":
                return LogLevel.Debug;
            case "info" or "information" or "nfo":
                return LogLevel.Information;
            case "warn" or "warning" or "wrn":
                return LogLevel.Warning;
            case "error" or "err":
                return LogLevel.Error;
            case "critical" or "crit" or "fatal":
                return LogLevel.Critical;
            case "none" or "off" or "quiet" or "silent":
                return LogLevel.None;
            case "" or "0" or "default":
                return fallback;
            default:
                note = $"LogLevel \"{text}\" is not a level name; using {fallback}";
                return fallback;
        }
    }

    public void Line(LogLevel level, string message, string? color = null)
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
            string tagColor = level switch
            {
                LogLevel.Trace => "\x1b[90m",
                LogLevel.Debug or LogLevel.Warning => "\x1b[33m",
                LogLevel.Information => "\x1b[32m",
                _ => "\x1b[31m",
            };
            console = Color
                ? $"\x1b[0m[{tagColor}{tag}\x1b[0m] [{stamp}]: {color}{message}\x1b[0m\n"
                : $"[{tag}] [{stamp}]: {message}\n";
        }
        try
        {
            _queue.Add((level, $"{stamp} [{tag}] {message}", console, null));
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Drains the queue to the file and the console. The file is optional: if it cannot be opened,
    /// or fails mid-run (a full disk), it is dropped, <see cref="FileError"/> says why, and the
    /// console keeps going, so a log problem never stops the game or a waiting <see cref="Flush"/>.
    /// </summary>
    private void WriteLoop()
    {
        StreamWriter? writer = FileError == null ? TryOpenFile() : null;
        long lastFlush = Environment.TickCount64;
        while (true)
        {
            if (!_queue.TryTake(out var item, FlushIntervalMs))
            {
                if (_queue.IsCompleted)
                {
                    break;
                }

                writer = TryWrite(writer, w => w.Flush());
                lastFlush = Environment.TickCount64;
                continue;
            }

            if (item.Text != null)
            {
                writer = TryWrite(writer, w => w.WriteLine(item.Text));
                try
                {
                    if (item.Console != null)
                    {
                        ConsoleWriter?.Invoke(item.Console);
                    }
                }
                catch
                {
                }
            }

            // Warnings, explicit flushes, and a steady stream of lines that never goes quiet:
            // a crash in the middle of a busy startup must still leave its last lines on disk.
            if (item.Level >= LogLevel.Warning || item.Flushed != null || _queue.Count == 0 && _queue.IsAddingCompleted
                || Environment.TickCount64 - lastFlush >= FlushIntervalMs)
            {
                writer = TryWrite(writer, w => w.Flush());
                lastFlush = Environment.TickCount64;
            }

            item.Flushed?.Set();
        }

        TryWrite(writer, w => w.Dispose());
    }

    private StreamWriter? TryOpenFile()
    {
        try
        {
            return new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 14), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FileError = e;
            return null;
        }
    }

    /// <summary>Runs one file operation; on failure the file is given up and null comes back.</summary>
    private StreamWriter? TryWrite(StreamWriter? writer, Action<StreamWriter> action)
    {
        if (writer == null)
        {
            return null;
        }

        try
        {
            action(writer);
            return writer;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            FileError ??= e;
            try
            {
                writer.Dispose();
            }
            catch
            {
            }

            return null;
        }
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
                Archive(Path, ArchivePath(Path, _name, LaunchTime, ArchiveSuffix));
            }
            catch { }
        }
    }

    public void Dispose() => Close();

    /// <summary>Creates the directory, archives a leftover file and truncates; the error if any of that fails.</summary>
    private static Exception? PrepareSession(string directory, string path, string name)
    {
        try
        {
            Directory.CreateDirectory(directory);
            ArchiveLeftover(path, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e;
        }

        return Truncate(path);
    }

    private static Exception? Truncate(string path)
    {
        try
        {
            File.WriteAllText(path, "");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e;
        }
    }

    /// <summary>
    /// Keeps the last <see cref="HotArchiveAge"/> of "&lt;name&gt;_&lt;launch time&gt;.log" archives as
    /// they are and compresses every older one to "&lt;file&gt;.zst", keeping its timestamp, so the
    /// directory holds a week of logs anyone can open and a compact history behind them. Age comes
    /// from the launch time in the file name, or its last write time when the name does not
    /// parse. Any file that cannot be compressed is left alone. Returns the files compressed.
    /// </summary>
    public static List<string> CompressColdArchives(string directory, string name, DateTime now)
    {
        var compressed = new List<string>();
        IEnumerable<string> archives;
        try
        {
            archives = Directory.EnumerateFiles(directory, $"{name}_*.log");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return compressed;
        }

        foreach (string archive in archives)
        {
            try
            {
                // The stamp follows the name; whatever follows the stamp (a match id, an opponent) is not its concern.
                string rest = System.IO.Path.GetFileNameWithoutExtension(archive)[(name.Length + 1)..];
                string stamp = rest.Length >= ArchiveStampFormat.Length ? rest[..ArchiveStampFormat.Length] : rest;
                if (!DateTime.TryParseExact(stamp, ArchiveStampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime launch))
                {
                    launch = File.GetLastWriteTime(archive);
                }

                if (now - launch < HotArchiveAge)
                {
                    continue;
                }

                string target = archive + ".zst";
                if (!File.Exists(target))
                {
                    string temp = target + ".tmp";
                    using (var input = File.OpenRead(archive))
                    using (var output = File.Create(temp))
                    using (var zstd = new ZstdSharp.CompressionStream(output, ArchiveCompressionLevel))
                    {
                        input.CopyTo(zstd);
                    }

                    File.SetLastWriteTime(temp, File.GetLastWriteTime(archive));
                    File.Move(temp, target, overwrite: true);
                }

                File.Delete(archive);
                compressed.Add(target);
            }
            catch (Exception)
            {
                // A file that cannot be read, written or compressed is left for the next launch.
            }
        }

        return compressed;
    }

    private static string ArchivePath(string path, string name, DateTime launch, string suffix = "") =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{name}_{launch.ToString(ArchiveStampFormat, CultureInfo.InvariantCulture)}{suffix}.log");

    /// <summary>Whether the file's last line is the closed marker, read from its tail.</summary>
    private static bool EndsWithClosedMarker(string path)
    {
        using var file = File.OpenRead(path);
        int tail = (int)Math.Min(256, file.Length);
        file.Seek(-tail, SeekOrigin.End);
        var bytes = new byte[tail];
        file.ReadExactly(bytes);
        string text = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n', ' ');
        return text.EndsWith(ClosedMarker, StringComparison.Ordinal);
    }

    private static void Archive(string path, string target)
    {
        if (!File.Exists(target))
        {
            File.Copy(path, target);
        }
    }

    /// <summary>
    /// The previous run's file, if it was never archived (its process ended without a Close). A
    /// file that ends with the closed marker was archived by its Close and is left alone: a match
    /// log stays on disk after it closes, and archiving it again under its first line's time gave
    /// two copies of every match a second apart (2026-09-24).
    /// </summary>
    private static void ArchiveLeftover(string path, string name)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0 || EndsWithClosedMarker(path))
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
