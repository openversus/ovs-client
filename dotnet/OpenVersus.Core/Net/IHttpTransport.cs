namespace OpenVersus.Net;

/// <summary>One HTTP exchange: whether it succeeded, and what came back.</summary>
/// <param name="Ok">True for a 2xx response.</param>
/// <param name="Status">The status code; 0 when no response arrived.</param>
/// <param name="Body">The response body; empty when there was none.</param>
/// <param name="Error">What went wrong before a response arrived, or null.</param>
public sealed record HttpResult(bool Ok, int Status, byte[] Body, string? Error)
{
    /// <summary>The body as UTF-8.</summary>
    public string Text => System.Text.Encoding.UTF8.GetString(Body);
    /// <summary>A result for a request that got no response, with <paramref name="error"/> saying why.</summary>
    public static HttpResult Failed(string error) => new(false, 0, [], error);
}

/// <summary>
/// The client's HTTP. One implementation on WinHTTP, which is what the C++ used on Proton and
/// for every download and poll; anything else (HttpClient, WinInet) plugs in here.
/// </summary>
public interface IHttpTransport
{
    /// <summary>Sends a GET. A request that gets no response comes back as a result with <see cref="HttpResult.Ok"/> false, not as an exception.</summary>
    HttpResult Get(Uri url, TimeSpan timeout);
    /// <summary>Sends a POST of <paramref name="body"/> as <paramref name="contentType"/>. A request that gets no response comes back as a result with <see cref="HttpResult.Ok"/> false, not as an exception.</summary>
    HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout);
}

/// <summary>The server URL as the ini gives it, turned into request URLs.</summary>
public static class Urls
{
    /// <summary>The C++ parsers treated a URL without a scheme as http; so does this.</summary>
    public static Uri? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!text.Contains("://"))
        {
            text = "http://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") ? uri : null;
    }

    /// <summary>Base URL with the trailing slash removed, plus a path.</summary>
    public static Uri? Join(string baseUrl, string path)
    {
        var b = Parse(baseUrl);
        return b == null ? null : new Uri(b.GetLeftPart(UriPartial.Path).TrimEnd('/') + path);
    }
}
