using System.Globalization;
using OpenVersus.Native;

namespace OpenVersus.Config;

/// <summary>One ini file, read and written through the profile API (see <see cref="Profile"/>).</summary>
public sealed unsafe class IniFile(string path)
{
    public string Path { get; } = path;

    public string ReadString(string section, string key, string defaultValue)
    {
        // The C++ used a 255-character buffer; the longest shipped pattern is close to that, so
        // this one is generous.
        var buffer = new char[8192];
        fixed (char* p = buffer)
        {
            uint length = Profile.GetPrivateProfileString(section, key, defaultValue, p, (uint)buffer.Length, Path);
            return new string(p, 0, (int)length);
        }
    }

    /// <summary>"true" or "True" is true, anything else is false, as the C++ ReadBoolean decided.</summary>
    public bool ReadBool(string section, string key, bool defaultValue) =>
        ReadString(section, key, defaultValue ? "true" : "false") is "true" or "True";

    public ulong ReadUInt64(string section, string key, ulong defaultValue) =>
        ulong.TryParse(ReadString(section, key, defaultValue.ToString(CultureInfo.InvariantCulture)).Trim(),
            NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : defaultValue;

    public void WriteString(string section, string key, string value) => Profile.WritePrivateProfileString(section, key, value, Path);

    public void WriteBool(string section, string key, bool value) => WriteString(section, key, value ? "true" : "false");

    public void WriteUInt64(string section, string key, ulong value) => WriteString(section, key, value.ToString(CultureInfo.InvariantCulture));
}
