namespace OpenVersus.Config;

/// <summary>
/// What every settings file shares, TOML or legacy ini: the boolean spellings, quote stripping,
/// and writing a file without ever leaving half of one. Kept apart from <see cref="IniFile"/> so
/// that dropping the legacy ini reader one day is a single file.
/// </summary>
public static class ConfigFile
{
    /// <summary>"true"/"false", "on"/"off" and "1"/"0", in any case and with surrounding whitespace; nothing else.</summary>
    public static bool TryParseBool(string? text, out bool value)
    {
        string trimmed = text?.Trim() ?? "";
        value = trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) || trimmed == "1";
        return value || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) || trimmed == "0";
    }

    /// <summary>Writes through a temporary file beside <paramref name="path"/>, so a failed write
    /// never leaves half a file. Returns why it failed, or null.</summary>
    internal static Exception? WriteAtomically(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return e;
        }
    }

    /// <summary>The value without one pair of matching surrounding quotes, as the profile API read it.</summary>
    internal static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;
}
