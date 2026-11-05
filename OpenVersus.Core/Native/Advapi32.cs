using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>The registry reads the identity code uses. Names follow the Windows SDK.</summary>
public static partial class Advapi32
{
    /// <summary>HKEY_CURRENT_USER.</summary>
    public static readonly nint HKEY_CURRENT_USER = unchecked((nint)(int)0x80000001);
    /// <summary>HKEY_LOCAL_MACHINE.</summary>
    public static readonly nint HKEY_LOCAL_MACHINE = unchecked((nint)(int)0x80000002);
    /// <summary>RRF_RT_REG_SZ: accept only a REG_SZ value.</summary>
    public const uint RRF_RT_REG_SZ = 0x2;

    /// <summary>RegGetValueW: ERROR_SUCCESS (0) or a Win32 error; <paramref name="size"/> is in bytes and comes back as the bytes written, NUL included.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegGetValue(nint key, string subKey, string value, uint flags, nint type, Span<byte> data, ref uint size);

    /// <summary>A REG_SZ value under HKEY_LOCAL_MACHINE, or "" when it cannot be read.</summary>
    public static string ReadLocalMachineString(string subKey, string value) => ReadString(HKEY_LOCAL_MACHINE, subKey, value);

    /// <summary>A REG_SZ value under HKEY_CURRENT_USER, or "" when it cannot be read.</summary>
    public static string ReadCurrentUserString(string subKey, string value) => ReadString(HKEY_CURRENT_USER, subKey, value);

    private static string ReadString(nint root, string subKey, string value)
    {
        Span<byte> buffer = stackalloc byte[256];
        uint size = (uint)buffer.Length;
        if (RegGetValue(root, subKey, value, RRF_RT_REG_SZ, 0, buffer, ref size) != 0)
        {
            return "";
        }

        string text = System.Text.Encoding.Unicode.GetString(buffer[..(int)Math.Min(size, (uint)buffer.Length)]);
        int nul = text.IndexOf('\0');
        return nul < 0 ? text : text[..nul];
    }
}
