namespace OpenVersus.Tests;

/// <summary>
/// Permission changes for the "cannot write here" tests. Only Unix has these modes, so every
/// test that uses them starts with <see cref="SkipUnlessUnix"/>; the guards inside keep the
/// platform analyzer satisfied on the Windows build of the tests.
/// </summary>
internal static class UnixPermissions
{
    public static void SkipUnlessUnix() => Skip.If(OperatingSystem.IsWindows(), "needs Unix file permissions");

    public static void MakeReadOnly(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    public static void MakeUnreadable(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.None);
        }
    }

    public static void Restore(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
