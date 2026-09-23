using System.Text;

namespace OpenVersus;

public enum LogLevel { Debug, Info, Warn, Error, Critical }

/// <summary>
/// The file next to the plugin is truncated at startup and appended per line, so a hang or
/// crash part-way still leaves everything up to that point on disk. The console mirror uses
/// the same prefixes and colours the C++ client printed, so his eye still knows where to look.
/// </summary>
public sealed class Log
{
	private readonly string _path;
	private readonly object _lock = new();

	public Log(string path)
	{
		_path = path;
		File.WriteAllText(_path, "");
	}

	/// <summary>Where console lines go once a console window exists; null means no console.</summary>
	public Action<string>? ConsoleWriter { get; set; }
	/// <summary>Whether Debug lines are written at all. The C++ DebugLogging setting.</summary>
	public bool DebugEnabled { get; set; }
	/// <summary>Colour escapes in the console mirror; the file never has them.</summary>
	public bool Colour { get; set; } = true;

	public void Debug(string message) { if (DebugEnabled) Line(LogLevel.Debug, message); }
	public void Info(string message) => Line(LogLevel.Info, message);
	public void Warn(string message) => Line(LogLevel.Warn, message);
	public void Error(string message) => Line(LogLevel.Error, message);
	public void Critical(string message) => Line(LogLevel.Critical, message);
	/// <summary>Info, shown green on the console: the C++ printfSuccess.</summary>
	public void Success(string message) => Line(LogLevel.Info, message, "\x1b[32m");

	public void Line(LogLevel level, string message, string? colour = null)
	{
		string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
		string tag = level switch
		{
			LogLevel.Debug => "DBG",
			LogLevel.Info => "NFO",
			LogLevel.Warn => "WRN",
			LogLevel.Error => "ERR",
			_ => "CRT",
		};
		lock (_lock)
		{
			try { File.AppendAllText(_path, $"{stamp} [{tag}] {message}{Environment.NewLine}"); }
			catch { /* a full or read-only disk must not stop the game */ }
			if (ConsoleWriter != null)
			{
				try
				{
					string tagColour = level switch
					{
						LogLevel.Debug or LogLevel.Warn => "\x1b[33m",
						LogLevel.Info => "\x1b[32m",
						_ => "\x1b[31m",
					};
					// Same shape as the C++ console: "[TAG] [timestamp]: message".
					string text = Colour
						? $"\x1b[0m[{tagColour}{tag}\x1b[0m] [{stamp}]: {colour}{message}\x1b[0m"
						: $"[{tag}] [{stamp}]: {message}";
					ConsoleWriter(text + "\n");
				}
				catch { }
			}
		}
	}

	/// <summary>Bytes as the log shows them everywhere: upper-case hex, no separators.</summary>
	public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
