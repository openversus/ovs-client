using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Tomlyn.Syntax;

namespace OpenVersus.Config;

/// <summary>
/// Turns a legacy OpenVersus.ini into OpenVersus.toml, line for line. Every setting is carried
/// as it is, deprecated or invalid ones included, with one exception: both ServerUrl keys become
/// <see cref="OvsVersion.DefaultServerUrl"/>, since old files can still point at the plain-http
/// endpoint that only exists for them. Comments move from ';' to '#' in place; section and key
/// names take the spelling of the row they match; values the row reads as a boolean become TOML
/// booleans and everything else a string. What the ini reader never read (a repeated section, a
/// repeated key, a line that is neither) becomes a comment, so the text stays and nothing
/// changes meaning. <see cref="Verify"/> then checks the result against the ini reader itself.
/// </summary>
internal static class SettingsMigration
{
    /// <summary>The TOML text for <paramref name="ini"/>.</summary>
    public static string Convert(IniFile ini, ILogger? log)
    {
        var lines = new List<string>();
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        bool read = true;

        foreach (IniFile.Line line in ini.Lines)
        {
            switch (line.Kind)
            {
                case IniFile.Kind.Blank:
                    lines.Add(line.Text);
                    break;
                case IniFile.Kind.Comment:
                    lines.Add(Comment(line.Text));
                    break;
                case IniFile.Kind.Section:
                    section = CanonicalSection(line.Name!);
                    keys.Clear();
                    read = sections.Add(section);
                    lines.Add(read ? Header(line.Text, section) : "# " + Clean(line.Text));
                    break;
                case IniFile.Kind.KeyValue when read && line.Name!.Length > 0 && keys.Add(line.Name):
                    lines.Add(KeyValue(line, section, log));
                    break;
                default:
                    // A key line's Text stops before the value; ToString is the whole line.
                    lines.Add("# " + Clean(line.ToString()));
                    break;
            }
        }

        string text = string.Join(ini.NewLine, lines);
        return ini.FinalNewLine && lines.Count > 0 ? text + ini.NewLine : text;
    }

    /// <summary>
    /// Null when <paramref name="toml"/> parses and gives, for every key in the ini, what the ini
    /// reader gives (a boolean row compared as a boolean, a ServerUrl row against the default);
    /// otherwise what differs.
    /// </summary>
    public static string? Verify(IniFile ini, string toml)
    {
        TomlConfig converted = TomlConfig.FromText(toml, "");
        if (converted.Errors.Count > 0)
        {
            return $"the converted file does not parse ({converted.Errors[0]})";
        }

        string? section = null;
        foreach (IniFile.Line line in ini.Lines)
        {
            if (line.Kind == IniFile.Kind.Section)
            {
                section = line.Name;
            }
            else if (line.Kind == IniFile.Kind.KeyValue && section != null && line.Name!.Length > 0)
            {
                SettingDef? row = Settings.Row(section, line.Name);
                string? expected = IsServerUrl(row) ? OvsVersion.DefaultServerUrl : ini.Get(section, line.Name);
                string? actual = converted.Get(section, line.Name);
                bool same = row?.Kind == SettingKind.Bool && IniFile.TryParseBool(expected, out bool a) && IniFile.TryParseBool(actual, out bool b)
                    ? a == b
                    : expected == actual;
                if (!same)
                {
                    return $"[{section}] {line.Name} reads \"{expected}\" from the ini and \"{actual}\" from the TOML";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Takes the ini's settings into an existing TOML: a row's value only where the TOML is missing
    /// it or still has the default (so nothing changed in the TOML is overwritten), any other key
    /// only where the TOML lacks it, and never ServerUrl. Values are typed as the conversion types
    /// them. Returns how many were taken; each is logged.
    /// </summary>
    public static int Merge(IniFile ini, TomlConfig toml, ILogger? log)
    {
        int taken = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (IniFile.Line line in ini.Lines)
        {
            if (line.Kind == IniFile.Kind.Section)
            {
                section = line.Name;
                continue;
            }

            if (line.Kind != IniFile.Kind.KeyValue || section == null || line.Name!.Length == 0 || !seen.Add(section + "\n" + line.Name))
            {
                continue;
            }

            SettingDef? row = Settings.Row(section, line.Name);
            string? value = ini.Get(section, line.Name);
            if (value == null || IsServerUrl(row))
            {
                continue;
            }

            string? current;
            if (row != null)
            {
                current = toml.Get(row.Section, row.Key);
                if ((current != null && !Same(row, current, row.Default)) || Same(row, value, current ?? row.Default))
                {
                    continue;
                }

                toml.Set(row.Section, row.Key, row.Kind, value);
                log?.LogInformation("[Settings] {Row} = {Value}, from {Legacy}", row, value, Settings.LegacyFileName);
            }
            else
            {
                RetiredSetting? retired = Settings.Retired(section, line.Name);
                string tomlSection = retired?.Section ?? CanonicalSection(section);
                string key = retired?.Key ?? line.Name;
                if (toml.Get(tomlSection, key) != null)
                {
                    continue;
                }

                toml.Add(tomlSection, key, SettingKind.String, value);
                log?.LogInformation("[Settings] [{Section}] {Key} = {Value}, from {Legacy}", tomlSection, key, value, Settings.LegacyFileName);
            }

            taken++;
        }

        return taken;
    }

    /// <summary>Whether two texts are the same value of <paramref name="row"/>: as booleans for a boolean row.</summary>
    private static bool Same(SettingDef row, string a, string b) =>
        row.Kind == SettingKind.Bool && IniFile.TryParseBool(a, out bool x) && IniFile.TryParseBool(b, out bool y) ? x == y : a == b;

    private static bool IsServerUrl(SettingDef? row) => row == Settings.Rows.ServerUrl || row == Settings.Rows.ProdServerUrl;

    private static string CanonicalSection(string name)
    {
        foreach (SettingDef def in Settings.Table)
        {
            if (string.Equals(def.Section, name, StringComparison.OrdinalIgnoreCase))
            {
                return def.Section;
            }
        }

        return name;
    }

    /// <summary>The header with its indentation and anything after the ']' as a comment.</summary>
    private static string Header(string text, string section)
    {
        string indent = text[..(text.Length - text.TrimStart().Length)];
        string rest = text[(text.IndexOf(']') + 1)..];
        string trailer = rest.Trim().Length == 0 ? "" : rest.TrimStart().StartsWith(';') ? Comment(rest) : " # " + Clean(rest.Trim());
        return indent + "[" + TableName(section) + "]" + trailer;
    }

    private static string KeyValue(IniFile.Line line, string? section, ILogger? log)
    {
        // line.Text runs up to the value: the key, its spacing and the '='.
        string text = line.Text;
        int equals = text.IndexOf('=');
        string indent = text[..(text.Length - text.TrimStart().Length)];
        string beforeEquals = text[..equals];
        string spaceBefore = beforeEquals[beforeEquals.TrimEnd().Length..];
        string spaceAfter = text[(equals + 1)..];

        SettingDef? row = section == null ? null : Settings.Row(section, line.Name!);
        string key = row?.Key ?? (section == null ? null : Settings.Retired(section, line.Name!)?.Key) ?? line.Name!;
        string value = IniFile.Unquote(line.Value.Trim());

        string literal;
        if (IsServerUrl(row))
        {
            literal = Quote(OvsVersion.DefaultServerUrl);
            if (value != OvsVersion.DefaultServerUrl)
            {
                log?.LogInformation("[Settings] {Row} was {Old}; the converted file has {New}", row, value, OvsVersion.DefaultServerUrl);
            }
        }
        else if (row?.Kind == SettingKind.Bool && IniFile.TryParseBool(value, out bool b))
        {
            literal = b ? "true" : "false";
        }
        else
        {
            literal = Quote(value);
        }

        return indent + Key(key) + spaceBefore + "=" + spaceAfter + literal;
    }

    /// <summary>An ini comment as a TOML one: the first ';' becomes '#', the rest stays.</summary>
    private static string Comment(string text)
    {
        int semicolon = text.IndexOf(';');
        return Clean(text[..semicolon] + "#" + text[(semicolon + 1)..]);
    }

    /// <summary>A TOML comment cannot hold control characters other than tab.</summary>
    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            clean.Append((c < ' ' && c != '\t') || c == '\x7F' ? '?' : c);
        }

        return clean.ToString();
    }

    private static string TableName(string section)
    {
        string[] parts = section.Split('.');
        return parts.All(p => p.Trim().Length > 0)
            ? string.Join('.', parts.Select(p => Key(p.Trim())))
            : Quote(section);
    }

    private static string Key(string key) => BareKeySyntax.IsBareKey(key) ? key : Quote(key);

    /// <summary>A TOML basic string.</summary>
    internal static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '\t':
                    quoted.Append("\\t");
                    break;
                case < ' ' or '\x7F':
                    quoted.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
