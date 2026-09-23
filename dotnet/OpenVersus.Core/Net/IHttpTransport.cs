namespace OpenVersus.Net;

public sealed record HttpResult(bool Ok, int Status, byte[] Body, string? Error)
{
	public string Text => System.Text.Encoding.UTF8.GetString(Body);
	public static HttpResult Failed(string error) => new(false, 0, [], error);
}

/// <summary>
/// The client's HTTP. One implementation on WinHTTP, which is what the C++ used on Proton and
/// for every download and poll; anything else (HttpClient, WinInet) plugs in here.
/// </summary>
public interface IHttpTransport
{
	HttpResult Get(Uri url, TimeSpan timeout);
	HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout);
}

public static class Urls
{
	/// <summary>The C++ parsers treated a URL without a scheme as http; so does this.</summary>
	public static Uri? Parse(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		if (!text.Contains("://")) text = "http://" + text;
		return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") ? uri : null;
	}

	/// <summary>Base URL with the trailing slash removed, plus a path.</summary>
	public static Uri? Join(string baseUrl, string path)
	{
		var b = Parse(baseUrl);
		return b == null ? null : new Uri(b.GetLeftPart(UriPartial.Path).TrimEnd('/') + path);
	}
}
