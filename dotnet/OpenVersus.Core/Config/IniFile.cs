using System.Globalization;
using System.Text;

namespace OpenVersus.Config;

/// <summary>
/// One ini file, kept line for line. Reading follows the Windows profile API the C++ client used,
/// since every file players have was read through it: section and key names match regardless of
/// case, values are trimmed and lose surrounding quotes, a line starting with ';' is a comment and
/// the first of two duplicate keys wins. Writing changes only the value of an existing key, or adds
/// a missing key at the end of its section in the file's own spacing style. Blank lines, comments,
/// unknown keys, line endings and encoding stay exactly as the player left them, and
/// <see cref="Save"/> writes nothing unless something changed.
/// </summary>
public sealed class IniFile
{
    private enum Kind
    {
        Blank, Comment, Section, KeyValue, Other
    }

    /// <summary>One line of the file. For a key line, Text is everything up to and including the
    /// '=' and any spacing after it, so a value can be replaced without disturbing the rest.</summary>
    private sealed class Line(Kind kind, string text, string? name = null, string value = "")
    {
        public Kind Kind { get; } = kind;
        public string Text { get; set; } = text;
        /// <summary>The section name for a header, the key for a key line.</summary>
        public string? Name { get; } = name;
        /// <summary>The value as written, quotes and all; only meaningful for a key line.</summary>
        public string Value { get; set; } = value;

        public override string ToString() => Kind == Kind.KeyValue ? Text + Value : Text;
    }

    private static readonly Encoding s_utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly List<Line> _lines = [];
    private readonly string _newLine;
    private readonly bool _finalNewLine;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private bool _dirty;

    public string Path { get; }

    /// <summary>Why the file could not be read, in which case this is an empty document, as the
    /// profile API handed back defaults for a file it could not open.</summary>
    public Exception? LoadError { get; private init; }

    /// <summary>Why the last <see cref="Save"/> could not write, or null when it could.</summary>
    public Exception? SaveError { get; private set; }

    private IniFile(string path, string? text, Encoding encoding, byte[] preamble)
    {
        Path = path;
        _encoding = encoding;
        _preamble = preamble;
        if (string.IsNullOrEmpty(text))
        {
            // A file that does not exist yet, or was emptied by hand, is written the way the C++
            // client's files were: CRLF.
            _newLine = "\r\n";
            _finalNewLine = true;
            return;
        }

        _newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        _finalNewLine = text.Length == 0 || text.EndsWith('\n');
        string[] raw = text.Split('\n');
        int count = _finalNewLine && text.Length > 0 ? raw.Length - 1 : raw.Length;
        for (int i = 0; i < count; i++)
        {
            _lines.Add(Parse(raw[i].TrimEnd('\r')));
        }
    }

    /// <summary>Reads the file, or starts an empty document for a path that does not exist yet.</summary>
    public static IniFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new IniFile(path, null, s_utf8, []);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new IniFile(path, null, s_utf8, []) { LoadError = e };
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new IniFile(path, s_utf8.GetString(bytes, 3, bytes.Length - 3), s_utf8, [0xEF, 0xBB, 0xBF]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new IniFile(path, Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, [0xFF, 0xFE]);
        }

        try
        {
            return new IniFile(path, s_utf8Strict.GetString(bytes), s_utf8, []);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8, so a file written by an ANSI code page; Latin-1 maps every byte to a
            // character and back, so whatever is there survives a save untouched.
            return new IniFile(path, Encoding.Latin1.GetString(bytes), Encoding.Latin1, []);
        }
    }

    /// <summary>The value of a key, or null when the section or key is missing.</summary>
    public string? Get(string section, string key)
    {
        int index = FindKey(section, key);
        return index < 0 ? null : Unquote(_lines[index].Value.Trim());
    }

    public string Get(string section, string key, string defaultValue) => Get(section, key) ?? defaultValue;

    /// <summary>A boolean, or the default when the key is missing or is not a spelling <see cref="TryParseBool"/> accepts.</summary>
    public bool GetBool(string section, string key, bool defaultValue) => TryParseBool(Get(section, key), out bool value) ? value : defaultValue;

    public ulong GetUInt64(string section, string key, ulong defaultValue) =>
        ulong.TryParse(Get(section, key), NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : defaultValue;

    /// <summary>"true"/"false", "on"/"off" and "1"/"0", in any case and with surrounding whitespace; nothing else.</summary>
    public static bool TryParseBool(string? text, out bool value)
    {
        string trimmed = text?.Trim() ?? "";
        value = trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) || trimmed == "1";
        return value || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) || trimmed == "0";
    }

    /// <summary>
    /// Sets a value. An existing key keeps its line and spacing and only the value changes; a
    /// missing key is added at the end of its section, and a missing section at the end of the file.
    /// </summary>
    public void Set(string section, string key, string value)
    {
        int index = FindKey(section, key);
        if (index >= 0)
        {
            Line line = _lines[index];
            if (Unquote(line.Value.Trim()) != value)
            {
                line.Value = value;
                _dirty = true;
            }

            return;
        }

        int header = FindSection(section);
        if (header < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Kind != Kind.Blank && UsesBlankSeparators())
            {
                _lines.Add(new Line(Kind.Blank, ""));
            }

            _lines.Add(new Line(Kind.Section, $"[{section}]", section));
            header = _lines.Count - 1;
        }

        int last = header;
        for (int i = header + 1; i < _lines.Count && _lines[i].Kind != Kind.Section; i++)
        {
            if (_lines[i].Kind != Kind.Blank)
            {
                last = i;
            }
        }

        string separator = SpacedEquals(header) ? " = " : "=";
        _lines.Insert(last + 1, new Line(Kind.KeyValue, key + separator, key, value));
        _dirty = true;
    }

    /// <summary>
    /// Writes the file if anything changed since it was loaded. Returns whether it did. A file that
    /// cannot be written (a read-only directory, say) is not an error worth stopping the plugin
    /// for: the values are still good in memory, so this returns false and keeps the reason in
    /// <see cref="SaveError"/>, as the profile API used to fail quietly.
    /// </summary>
    public bool Save()
    {
        SaveError = null;
        if (!_dirty)
        {
            return false;
        }

        var text = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i > 0)
            {
                text.Append(_newLine);
            }

            text.Append(_lines[i].ToString());
        }

        if (_finalNewLine && _lines.Count > 0)
        {
            text.Append(_newLine);
        }

        byte[] body = _encoding.GetBytes(text.ToString());
        byte[] bytes = new byte[_preamble.Length + body.Length];
        _preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, _preamble.Length);

        string temp = Path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveError = e;
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }

        _dirty = false;
        return true;
    }

    /// <summary>The text as it would be saved, for tests.</summary>
    public override string ToString() => string.Join(_newLine, _lines.Select(l => l.ToString())) + (_finalNewLine && _lines.Count > 0 ? _newLine : "");

    private static Line Parse(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new Line(Kind.Blank, text);
        }

        if (trimmed[0] == ';')
        {
            return new Line(Kind.Comment, text);
        }

        if (trimmed[0] == '[')
        {
            int close = trimmed.IndexOf(']');
            if (close > 0)
            {
                return new Line(Kind.Section, text, trimmed[1..close].Trim());
            }
        }

        int equals = text.IndexOf('=');
        if (equals > 0)
        {
            // Everything up to the first non-space character of the value stays with the key, so
            // "Key = Value" keeps its spaces when the value is replaced.
            int valueStart = equals + 1;
            while (valueStart < text.Length && text[valueStart] == ' ')
            {
                valueStart++;
            }

            return new Line(Kind.KeyValue, text[..valueStart], text[..equals].Trim(), text[valueStart..]);
        }

        return new Line(Kind.Other, text);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

    private int FindSection(string section)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Kind == Kind.Section && string.Equals(_lines[i].Name, section, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindKey(string section, string key)
    {
        int header = FindSection(section);
        if (header < 0)
        {
            return -1;
        }

        for (int i = header + 1; i < _lines.Count && _lines[i].Kind != Kind.Section; i++)
        {
            if (_lines[i].Kind == Kind.KeyValue && string.Equals(_lines[i].Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the file puts a blank line before its section headers. An empty file does,
    /// so that a file written from nothing comes out in the shape of sample.ini.</summary>
    private bool UsesBlankSeparators()
    {
        bool anySection = false;
        for (int i = 1; i < _lines.Count; i++)
        {
            if (_lines[i].Kind == Kind.Section)
            {
                anySection = true;
                if (_lines[i - 1].Kind == Kind.Blank)
                {
                    return true;
                }
            }
        }

        return !anySection;
    }

    /// <summary>Whether keys are written "Key = Value" rather than "Key=Value": decided by the
    /// section's own key lines, then the whole file's, and for a file without any, by sample.ini.</summary>
    private bool SpacedEquals(int header)
    {
        for (int i = header + 1; i < _lines.Count && _lines[i].Kind != Kind.Section; i++)
        {
            if (_lines[i].Kind == Kind.KeyValue)
            {
                return _lines[i].Text.Contains(" =");
            }
        }

        foreach (Line line in _lines)
        {
            if (line.Kind == Kind.KeyValue)
            {
                return line.Text.Contains(" =");
            }
        }

        return true;
    }
}
