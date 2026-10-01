using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using OpenVersus.Net;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Adds <see cref="RequestHeaders"/> to the game's HTTP requests. Every one of them, Hydra-built or
/// the engine's own, is set up in UE's FCurlHttpRequest::SetupRequest, the only place in the game
/// that gives curl a URL. Two of its curl_easy_setopt calls are redirected: CURLOPT_URL, which is
/// read and passed on unchanged, and CURLOPT_HTTPHEADER, where the headers are appended to the
/// game's list before it is passed on. The game skips that second call when its list is empty,
/// which no game request's is; appending to a non-empty list keeps its head, so the game frees our
/// nodes with its own. Both originals are always called, whatever the managed side does.
/// </summary>
public static unsafe class RequestHeadersHook
{
    /// <summary>The <see cref="GameFunctions"/> key for curl_easy_setopt; its URL and header calls in SetupRequest are redirected.</summary>
    public const string SetOptName = "CurlEasySetOpt";
    /// <summary>The <see cref="GameFunctions"/> key for curl_slist_append, which the headers are appended with.</summary>
    public const string SlistAppendName = "CurlSlistAppend";

    // The patterns live here, not in OpenVersus.toml: the identity headers are not something a player
    // can switch off or point elsewhere. Each is unique in the game's only build.
    /// <summary>The CURLOPT_URL call in SetupRequest.</summary>
    internal const string UrlPattern = "BA 12 27 00 00 48 8B 4F 60 E8 ? ? ? ?";
    /// <summary>The CURLOPT_HTTPHEADER call in SetupRequest.</summary>
    internal const string HeadersPattern = "4C 8B 47 68 4D 85 C0 74 ? 48 8B 4F 60 BA 27 27 00 00 E8 ? ? ? ?";
    /// <summary>The engine's call to curl_slist_append.</summary>
    internal const string SlistAppendPattern = "49 C7 C6 FF FF FF FF 48 8B 4F 68 E8 ? ? ? ?";

    // Where each pattern's call instruction is.
    private const int UrlCall = 9;
    private const int HeadersCall = 18;
    private const int SlistAppendCall = 11;

    private static delegate* unmanaged<nint, int, nint, int> s_setOpt;
    private static delegate* unmanaged<nint, byte*, nint> s_slistAppend;
    private static RequestHeaders? s_headers;

    /// <summary>
    /// Redirects both calls. False when a pattern is missing or no configured server URL parses; throws a
    /// <see cref="PatchException"/> when the two calls do not go to the same function, or a site is not a call.
    /// </summary>
    public static bool Apply(HookContext c, RequestHeaders headers)
    {
        c.Log.Info("==RequestHeaders==");
        if (headers.Servers.Count == 0)
        {
            c.Log.Warn("No server URL parses; no headers would ever be added. Skipping!");
            return false;
        }

        var url = c.Patterns.Find("CurlUrl", UrlPattern);
        var list = c.Patterns.Find("CurlHeaders", HeadersPattern);
        var append = c.Patterns.Find("CurlSlistAppend", SlistAppendPattern);
        if (!url.Found || !list.Found || !append.Found)
        {
            return false;
        }

        nint urlSite = url.Address + UrlCall, headersSite = list.Address + HeadersCall, appendSite = append.Address + SlistAppendCall;
        nint setOpt = CallSite.Destination(urlSite);
        if (CallSite.Destination(headersSite) != setOpt)
        {
            throw new PatchException($"the URL call at 0x{urlSite:X} goes to 0x{setOpt:X} and the header call at 0x{headersSite:X} to 0x{CallSite.Destination(headersSite):X}; expected one curl_easy_setopt");
        }

        s_headers = headers;
        s_slistAppend = (delegate* unmanaged<nint, byte*, nint>)CallSite.Destination(appendSite);
        s_setOpt = (delegate* unmanaged<nint, int, nint, int>)setOpt;
        CallSite.Redirect(urlSite, (nint)(delegate* unmanaged<nint, int, nint, int>)&SetUrl);
        CallSite.Redirect(headersSite, (nint)(delegate* unmanaged<nint, int, nint, int>)&SetHeaders);

        GameFunctions.Register(SetOptName, setOpt, FunctionSource.CallSite, $"calls at 0x{urlSite:X} and 0x{headersSite:X}", "CURLcode curl_easy_setopt(CURL* handle, CURLoption option, ...)", c.Image);
        GameFunctions.Register(SlistAppendName, (nint)s_slistAppend, FunctionSource.CallSite, $"call at 0x{appendSite:X}", "curl_slist* curl_slist_append(curl_slist* list, const char* text)", c.Image);
        c.Log.Success($"RequestHeaders: requests to {string.Join(", ", headers.Servers.Select(s => s.Authority))} get the client's headers");
        return true;
    }

    [UnmanagedCallersOnly]
    private static int SetUrl(nint handle, int option, nint url)
    {
        HookGuard.Run("RequestHeaders.Url", (handle, url), static s => s_headers?.NoteUrl(s.handle, s.url == 0 ? "" : Marshal.PtrToStringUTF8(s.url) ?? ""));
        return s_setOpt(handle, option, url);
    }

    [UnmanagedCallersOnly]
    private static int SetHeaders(nint handle, int option, nint list)
    {
        HookGuard.Run("RequestHeaders.Headers", (handle, list), static s => s_headers?.AppendNoted(s.handle, s.list, s_slistAppend));
        return s_setOpt(handle, option, list);
    }
}
