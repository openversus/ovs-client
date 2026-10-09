using System.Text;

namespace OpenVersus.Identity;

/// <summary>
/// Reads a JSON Web Token's two JSON parts without checking its signature: the client only looks at what a token
/// says (to log it, or to see when it expires); the server verifies. Nothing here trusts the contents.
/// </summary>
public static class Jwt
{
    /// <summary>The header and payload JSON of <paramref name="token"/>, or null when it is not three base64url parts.</summary>
    public static (string Header, string Payload)? Decode(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            return (Part(parts[0]), Part(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Part(string base64Url)
    {
        string base64 = base64Url.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
