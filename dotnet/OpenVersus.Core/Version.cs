namespace OpenVersus;

public static class OvsVersion
{
    /// <summary>
    /// The client version, as the C++ OVS_Version constant. It names the pattern-cache section
    /// and goes to the server in the identity and update checks. A test checks it against the
    /// repo's VERSION file so the two cannot drift.
    /// </summary>
    public const string Current = "2026.09.24.02";

    public const string Name = "OpenVersus";
    public const string ConsoleTitle = "OpenVersus Debug Console";
    /// <summary>The C++ default for both server URLs when the key is missing from the ini.</summary>
    public const string DefaultServerUrl = "https://prod.openversus.org/";
}
