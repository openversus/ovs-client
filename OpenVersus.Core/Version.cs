namespace OpenVersus;

/// <summary>
/// Names and defaults that identify the client. <c>Current</c>, the version, is generated at build
/// time from the VERSION file at the repository root (see OpenVersus.Core.csproj), so this file
/// never carries a number. It names the pattern-cache section and goes to the server in the
/// identity and update checks.
/// </summary>
public static partial class OvsVersion
{
    /// <summary>The client's name, for message box captions and the log.</summary>
    public const string Name = "OpenVersus";
    /// <summary>The debug console's window title.</summary>
    public const string ConsoleTitle = "OpenVersus Debug Console";
    /// <summary>The C++ default for both server URLs when the key is missing from the ini.</summary>
    public const string DefaultServerUrl = "https://prod.openversus.org/";
}
