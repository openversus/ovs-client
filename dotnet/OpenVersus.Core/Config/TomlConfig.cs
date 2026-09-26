using System.Globalization;
using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace OpenVersus.Config;

/// <summary>
/// One TOML file, kept byte for byte through Tomlyn's syntax tree. Tables and keys are looked up
/// regardless of case, as the ini reader did, and every value comes back as text for the caller
/// to parse. The table "" is the keys before the first table. A key is added right after the last
/// key of its table, in that table's "Key = Value" or "Key=Value" style, and a missing table goes
/// at the end of the file; a value that changes keeps the comment after it. Comments, blank lines,
/// spacing and line endings stay as the player left them, and <see cref="Save"/> writes nothing
/// unless something changed. A file that does not parse reads as empty and is never written;
/// <see cref="Errors"/> says where it broke.
/// </summary>
public sealed class TomlConfig
{
    private static readonly Encoding s_utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly DocumentSyntax _doc;
    private readonly string _newLine;
    private readonly bool _finalNewLine;
    private readonly byte[] _preamble;
    private bool _dirty;

    /// <summary>The file this document reads from and saves to.</summary>
    public string Path { get; }

    /// <summary>Why the file could not be read, in which case this is an empty document that is never saved.</summary>
    public Exception? LoadError { get; private init; }

    /// <summary>Where the file is not valid TOML ("line 3, column 13: ..."), in which case it reads
    /// as empty and is never saved. Empty when it parsed.</summary>
    public IReadOnlyList<string> Errors { get; private init; } = [];

    /// <summary>Why the last <see cref="Save"/> could not write, or null when it could.</summary>
    public Exception? SaveError { get; private set; }

    private bool Readable => LoadError == null && Errors.Count == 0;

    private bool _neverSave;

    private TomlConfig(string path, DocumentSyntax doc, string newLine, bool finalNewLine, byte[] preamble)
    {
        Path = path;
        _doc = doc;
        _newLine = newLine;
        _finalNewLine = finalNewLine;
        _preamble = preamble;
    }

    /// <summary>Reads the file, or starts an empty document for a path that does not exist yet.</summary>
    public static TomlConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return Parse("", path);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TomlConfig(path, new DocumentSyntax(), "\r\n", true, []) { LoadError = e };
        }

        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text;
        try
        {
            text = s_utf8Strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            return new TomlConfig(path, new DocumentSyntax(), "\r\n", true, []) { Errors = ["the file is not UTF-8 text"] };
        }

        return Parse(text, path, bom ? [0xEF, 0xBB, 0xBF] : []);
    }

    /// <summary>An empty document that <see cref="Save"/> never writes, for a run that must leave
    /// <paramref name="path"/> alone.</summary>
    public static TomlConfig InMemory(string path)
    {
        TomlConfig config = Parse("", path);
        config._neverSave = true;
        return config;
    }

    /// <summary>A document from text rather than a file; <see cref="Save"/> writes it to <paramref name="path"/>.</summary>
    public static TomlConfig FromText(string text, string path)
    {
        TomlConfig config = Parse(text, path);
        config._dirty = config.Readable;
        return config;
    }

    private static TomlConfig Parse(string text, string path, byte[]? preamble = null)
    {
        // A file written from nothing gets CRLF, as the ini files always had.
        string newLine = text.Length == 0 || text.Contains("\r\n") ? "\r\n" : "\n";
        DocumentSyntax doc = SyntaxParser.Parse(text, System.IO.Path.GetFileName(path), true);
        bool finalNewLine = text.Length == 0 || text.EndsWith('\n');
        return new TomlConfig(path, doc, newLine, finalNewLine, preamble ?? []) { Errors = ErrorsOf(doc) };
    }

    private static List<string> ErrorsOf(DocumentSyntax doc)
    {
        var errors = new List<string>();
        if (doc.Diagnostics == null)
        {
            return errors;
        }

        foreach (DiagnosticMessage d in doc.Diagnostics)
        {
            if (d.Kind == DiagnosticMessageKind.Error)
            {
                errors.Add($"line {d.Span.Start.Line + 1}, column {d.Span.Start.Column + 1}: {d.Message}");
            }
        }

        return errors;
    }

    /// <summary>The value of a key as text (true/false, digits, or the string itself), or null
    /// when the table or key is missing or the file did not parse.</summary>
    public string? Get(string section, string key)
    {
        KeyValueSyntax? kv = FindKey(ItemsOf(section), key);
        return kv?.Value == null ? null : ValueText(kv.Value);
    }

    /// <summary>
    /// Sets a key, typed as <see cref="Add"/> types it. An existing key keeps its line, spacing and
    /// trailing comment and only the value changes; a missing one is added. Does nothing when the
    /// file could not be read or parsed.
    /// </summary>
    public void Set(string section, string key, SettingKind kind, string value)
    {
        KeyValueSyntax? kv = FindKey(ItemsOf(section), key);
        if (kv?.Value == null)
        {
            Add(section, key, kind, value);
            return;
        }

        ValueSyntax literal = Literal(kind, value);
        if (literal.GetType() == kv.Value.GetType() && ValueText(literal) == ValueText(kv.Value))
        {
            return;
        }

        // The comment after a value and the spaces before it hang off the value's token.
        SyntaxToken? old = TokenOf(kv.Value);
        SyntaxToken? replacement = TokenOf(literal);
        if (old != null && replacement != null)
        {
            replacement.LeadingTrivia = old.LeadingTrivia;
            replacement.TrailingTrivia = old.TrailingTrivia;
        }

        kv.Value = literal;
        _dirty = true;
    }

    /// <summary>Every key in the file as (table, key), the table empty for keys before the first
    /// table and a dotted key spelled with its dots. Nothing when the file did not parse.</summary>
    public IEnumerable<(string Section, string Key)> Keys()
    {
        if (!Readable)
        {
            yield break;
        }

        foreach (KeyValueSyntax kv in _doc.KeyValues)
        {
            yield return ("", KeyText(kv.Key));
        }

        foreach (TableSyntaxBase table in _doc.Tables)
        {
            string name = KeyText(table.Name);
            foreach (KeyValueSyntax kv in table.Items)
            {
                yield return (name, KeyText(kv.Key));
            }
        }
    }

    /// <summary>
    /// Adds a key that is missing, typed by <paramref name="kind"/>: a boolean or a whole number
    /// when the text is one, otherwise a string. Does nothing when the key exists or the file could
    /// not be read or parsed, since writing then would replace what the player has.
    /// </summary>
    public void Add(string section, string key, SettingKind kind, string value)
    {
        if (!Readable || FindKey(ItemsOf(section), key) != null)
        {
            return;
        }

        var name = BareKeySyntax.IsBareKey(key) ? new KeySyntax(key) : new KeySyntax { Key = new StringValueSyntax(key) };
        var kv = new KeyValueSyntax(name, Literal(kind, value));
        if (section.Length == 0)
        {
            AddRoot(kv);
            return;
        }

        TableSyntax table = FindTable(section) as TableSyntax ?? AppendTable(section);
        if (!UsesSpacedEquals(table.Items))
        {
            kv.EqualToken!.LeadingTrivia = null;
            kv.EqualToken.TrailingTrivia = null;
        }

        // Whatever trails the table's last line (comments, blank lines) hangs off that line's end;
        // it moves to the new key's end, so the key sits directly under the last one.
        // A last line without a line ending gets one; Text() takes it off the end of the file again.
        KeyValueSyntax? last = LastItem(table);
        SyntaxToken? anchor = last != null ? last.EndOfLineToken : table.EndOfLineToken;
        if (anchor == null)
        {
            anchor = new SyntaxToken(TokenKind.NewLine, _newLine);
            SetEndOfLine(last, table, anchor);
        }

        kv.EndOfLineToken = new SyntaxToken(TokenKind.NewLine, _newLine) { TrailingTrivia = anchor.TrailingTrivia };
        anchor.TrailingTrivia = null;

        table.Items.Add(kv);
        _dirty = true;
    }

    /// <summary>A key before the first table: after the last such key, or first in the file.</summary>
    private void AddRoot(KeyValueSyntax kv)
    {
        if (!UsesSpacedEquals(_doc.KeyValues))
        {
            kv.EqualToken!.LeadingTrivia = null;
            kv.EqualToken.TrailingTrivia = null;
        }

        KeyValueSyntax? last = _doc.KeyValues.ChildrenCount == 0 ? null : _doc.KeyValues.GetChild(_doc.KeyValues.ChildrenCount - 1);
        kv.EndOfLineToken = new SyntaxToken(TokenKind.NewLine, _newLine);
        if (last != null)
        {
            last.EndOfLineToken ??= new SyntaxToken(TokenKind.NewLine, _newLine);
            kv.EndOfLineToken.TrailingTrivia = last.EndOfLineToken.TrailingTrivia;
            last.EndOfLineToken.TrailingTrivia = null;
        }
        else if (_doc.Tables.ChildrenCount > 0)
        {
            // The first table is separated from the keys above it by a blank line.
            kv.EndOfLineToken.TrailingTrivia = [new SyntaxTrivia(TokenKind.NewLine, _newLine)];
        }

        _doc.KeyValues.Add(kv);
        _dirty = true;
    }

    /// <summary>
    /// Writes the file if a key was added since it was loaded, or if it came from
    /// <see cref="FromText"/>. Returns whether it did. The text is parsed again first and not
    /// written if that fails. Like the ini files, a file that cannot be written is not an error
    /// worth stopping for: the reason is kept in <see cref="SaveError"/>.
    /// </summary>
    public bool Save()
    {
        SaveError = null;
        if (!_dirty || !Readable || _neverSave)
        {
            return false;
        }

        string text = Text();
        List<string> errors = ErrorsOf(SyntaxParser.Parse(text, System.IO.Path.GetFileName(Path), true));
        if (errors.Count > 0)
        {
            SaveError = new InvalidDataException($"the edited file would not parse ({errors[0]})");
            return false;
        }

        byte[] body = Encoding.UTF8.GetBytes(text);
        SaveError = IniFile.WriteAtomically(Path, [.. _preamble, .. body]);
        if (SaveError != null)
        {
            return false;
        }

        _dirty = false;
        return true;
    }

    /// <summary>The text as it would be saved.</summary>
    public override string ToString() => Text();

    /// <summary>The document's text, without a final line ending when the file had none.</summary>
    private string Text()
    {
        string text = _doc.ToString();
        return !_finalNewLine && text.EndsWith(_newLine, StringComparison.Ordinal) ? text[..^_newLine.Length] : text;
    }

    /// <summary>The keys of a table, or of the file's top for "", or null.</summary>
    private SyntaxList<KeyValueSyntax>? ItemsOf(string section) => !Readable ? null : section.Length == 0 ? _doc.KeyValues : FindTable(section)?.Items;

    private TableSyntaxBase? FindTable(string section)
    {
        if (!Readable)
        {
            return null;
        }

        foreach (TableSyntaxBase table in _doc.Tables)
        {
            if (table is TableSyntax && string.Equals(KeyText(table.Name), section, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return null;
    }

    private static KeyValueSyntax? FindKey(SyntaxList<KeyValueSyntax>? items, string key)
    {
        if (items == null)
        {
            return null;
        }

        foreach (KeyValueSyntax kv in items)
        {
            if (kv.Key?.DotKeys.ChildrenCount == 0 && string.Equals(KeyText(kv.Key), key, StringComparison.OrdinalIgnoreCase))
            {
                return kv;
            }
        }

        return null;
    }

    private static KeyValueSyntax? LastItem(TableSyntaxBase table) =>
        table.Items.ChildrenCount == 0 ? null : (KeyValueSyntax?)table.Items.GetChild(table.Items.ChildrenCount - 1);

    private static void SetEndOfLine(KeyValueSyntax? last, TableSyntaxBase table, SyntaxToken token)
    {
        if (last != null)
        {
            last.EndOfLineToken = token;
        }
        else
        {
            table.EndOfLineToken = token;
        }
    }

    private TableSyntax AppendTable(string section)
    {
        // The file's last line gets a line ending if it has none, so the header starts a line.
        if (_doc.Tables.ChildrenCount > 0)
        {
            TableSyntaxBase lastTable = _doc.Tables.GetChild(_doc.Tables.ChildrenCount - 1)!;
            KeyValueSyntax? last = LastItem(lastTable);
            if ((last != null ? last.EndOfLineToken : lastTable.EndOfLineToken) == null)
            {
                SetEndOfLine(last, lastTable, new SyntaxToken(TokenKind.NewLine, _newLine));
            }
        }
        else if (_doc.KeyValues.ChildrenCount > 0)
        {
            KeyValueSyntax last = _doc.KeyValues.GetChild(_doc.KeyValues.ChildrenCount - 1)!;
            last.EndOfLineToken ??= new SyntaxToken(TokenKind.NewLine, _newLine);
        }

        string[] parts = section.Split('.');
        var name = new KeySyntax(parts[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            name.DotKeys.Add(new DottedKeyItemSyntax(parts[i]));
        }

        var table = new TableSyntax(name) { EndOfLineToken = new SyntaxToken(TokenKind.NewLine, _newLine) };
        bool empty = _doc.Tables.ChildrenCount == 0 && _doc.KeyValues.ChildrenCount == 0;
        if (_doc.Tables.ChildrenCount == 0 && _doc.KeyValues.ChildrenCount > 0)
        {
            // Keys above the first table: AddRoot keeps the blank line under them, so this adds none.
            KeyValueSyntax lastRoot = _doc.KeyValues.GetChild(_doc.KeyValues.ChildrenCount - 1)!;
            lastRoot.EndOfLineToken!.TrailingTrivia ??= [new SyntaxTrivia(TokenKind.NewLine, _newLine)];
        }
        else if (!empty && UsesBlankSeparators())
        {
            table.OpenBracket!.LeadingTrivia = [new SyntaxTrivia(TokenKind.NewLine, _newLine)];
        }

        _doc.Tables.Add(table);
        return table;
    }

    /// <summary>Whether a blank line comes before the file's table headers. A file with at most
    /// one table does, so a file written from nothing is spaced out.</summary>
    private bool UsesBlankSeparators()
    {
        if (_doc.Tables.ChildrenCount < 2)
        {
            return true;
        }

        string text = _doc.ToString();
        return text.Contains(_newLine + _newLine + "[", StringComparison.Ordinal);
    }

    /// <summary>"Key = Value" or "Key=Value": the table's first key decides, then the file's first,
    /// and a file without keys gets the spaced form.</summary>
    private bool UsesSpacedEquals(SyntaxList<KeyValueSyntax> items)
    {
        KeyValueSyntax? first = items.ChildrenCount > 0 ? items.GetChild(0) : null;
        if (first == null && _doc.KeyValues.ChildrenCount > 0)
        {
            first = _doc.KeyValues.GetChild(0);
        }

        if (first == null)
        {
            foreach (TableSyntaxBase t in _doc.Tables)
            {
                if (t.Items.ChildrenCount > 0)
                {
                    first = t.Items.GetChild(0);
                    break;
                }
            }
        }

        if (first == null)
        {
            return true;
        }

        // The parser may hang the space before '=' off the key or off the '=' itself.
        return first.EqualToken?.LeadingTrivia is { Count: > 0 } || HasTrailingSpace(first.Key);
    }

    private static bool HasTrailingSpace(KeySyntax? key)
    {
        SyntaxNode? last = key?.DotKeys.ChildrenCount > 0 ? key.DotKeys.GetChild(key.DotKeys.ChildrenCount - 1)?.Key : key?.Key;
        return last?.TrailingTrivia is { Count: > 0 } || (last as BareKeySyntax)?.Key?.TrailingTrivia is { Count: > 0 }
            || (last as StringValueSyntax)?.Token?.TrailingTrivia is { Count: > 0 };
    }

    private static ValueSyntax Literal(SettingKind kind, string value)
    {
        if (kind == SettingKind.Bool && IniFile.TryParseBool(value, out bool b))
        {
            return new BooleanValueSyntax(b);
        }

        if (kind == SettingKind.Int && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
        {
            return new IntegerValueSyntax(n);
        }

        return new StringValueSyntax(value);
    }

    private static SyntaxToken? TokenOf(ValueSyntax value) => value switch
    {
        BooleanValueSyntax b => b.Token,
        IntegerValueSyntax i => i.Token,
        FloatValueSyntax f => f.Token,
        StringValueSyntax s => s.Token,
        _ => null,
    };

    private static string ValueText(ValueSyntax value) => value switch
    {
        BooleanValueSyntax b => b.Value ? "true" : "false",
        IntegerValueSyntax i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatValueSyntax f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        StringValueSyntax s => s.Value ?? "",
        _ => value.ToString().Trim(),
    };

    /// <summary>A key or table name as text: each part unquoted, joined with dots.</summary>
    internal static string KeyText(KeySyntax? key)
    {
        if (key == null)
        {
            return "";
        }

        var text = new StringBuilder(PartText(key.Key));
        foreach (DottedKeyItemSyntax dotted in key.DotKeys)
        {
            text.Append('.').Append(PartText(dotted.Key));
        }

        return text.ToString();
    }

    private static string PartText(BareKeyOrStringValueSyntax? part) => part switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? "",
        StringValueSyntax quoted => quoted.Value ?? "",
        null => "",
        _ => part.ToString().Trim(),
    };
}
