using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>
/// The Windows private-profile API. The C++ client read and wrote its ini files through it, so
/// using the same calls keeps OpenVersus.ini, OVSState.ini and PatternsCache.cache byte-compatible
/// with what players already have: same tolerance of "key = value" spacing, same rewrite of a
/// value in place, same appending of a missing key at the end of its section.
/// </summary>
public static unsafe partial class Profile
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetPrivateProfileString(string section, string key, string defaultValue, char* buffer, uint size, string file);

    [LibraryImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WritePrivateProfileString(string section, string key, string? value, string file);
}
