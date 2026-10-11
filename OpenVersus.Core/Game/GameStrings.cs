using System.Text;
using OpenVersus.Memory;

namespace OpenVersus.Game;

/// <summary>Reading the engine's string types out of memory, guarded, without calling the engine.</summary>
public static class GameStrings
{
    /// <summary>An FString (TArray of UTF-16 with a terminator) at <paramref name="address"/>; false if unreadable or implausibly long.</summary>
    public static bool TryReadFString(IMemory memory, nint address, out string value)
    {
        value = "";
        if (!memory.TryRead(address, out TArrayHeader header))
        {
            return false;
        }

        if (header.Count <= 1)
        {
            return true; // empty, with or without its terminator
        }

        if (header.Count > 1024 || header.Data == 0)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[(header.Count - 1) * 2];
        if (!memory.TryRead(header.Data, bytes))
        {
            return false;
        }

        value = Encoding.Unicode.GetString(bytes);
        return true;
    }

    /// <summary>Text made safe for a file name: letters, digits, dash and underscore, at most <paramref name="maxLength"/> characters.</summary>
    public static string ForFileName(string text, int maxLength = 32)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (char c in text)
        {
            if (sb.Length == maxLength)
            {
                break;
            }

            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return sb.ToString().Trim('_');
    }
}
