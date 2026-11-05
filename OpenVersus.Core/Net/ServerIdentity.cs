namespace OpenVersus.Net;

/// <summary>
/// Who this client is on its own calls to the OVS server (identify, notifications, the version
/// check): the install id, and the token /api/identify returned. The server resolves the player
/// from these (token, then Steam, Epic and install id) before it falls back to the IP, so two
/// players behind one IP don't drain each other's notifications. Only the server transport sends
/// them; downloads from GitHub never do. The install id stands in for an account for players
/// without a Steam or Epic id, so it is never logged.
/// </summary>
public sealed class ServerIdentity
{
    private volatile string _installId = "";
    private volatile string _token = "";

    /// <summary>This install's id, or "" when there is none.</summary>
    public string InstallId
    {
        get => _installId;
        set => _installId = Config.State.IsValidInstallId(value) ? value : "";
    }

    /// <summary>The token from the last successful /api/identify, or "". Anything that is not a plain JWT is dropped, as a header must not carry a line break.</summary>
    public string Token
    {
        get => _token;
        set => _token = IsTokenText(value) ? value : "";
    }

    /// <summary>The headers for the server's requests, each ending in CRLF; "" when there is nothing to send.</summary>
    public string Headers()
    {
        string installId = _installId, token = _token;
        return (installId.Length > 0 ? $"X-Install-Id: {installId}\r\n" : "")
            + (token.Length > 0 ? $"x-hydra-access-token: {token}\r\n" : "");
    }

    /// <summary>A JWT's characters only: base64url segments and dots.</summary>
    public static bool IsTokenText(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 4096 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
