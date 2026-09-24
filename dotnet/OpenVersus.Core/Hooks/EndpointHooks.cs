using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using OpenVersus.Net;

namespace OpenVersus.Hooks;

/// <summary>
/// Points the game at the OpenVersus servers. Two call sites: the game-server endpoint, which
/// the game reads as a narrow string, and the "prod" (WB network) endpoint, an FString.
/// </summary>
public static unsafe class EndpointHooks
{
    public const string GetEndpointKeyValueName = "GetEndpointKeyValue";
    public const string SetFStringValueName = "SetFStringValue";

    private static delegate* unmanaged<nint, byte*, nint> s_getEndpointKeyValue;
    private static delegate* unmanaged<nint, char*, nint> s_setFStringValue;
    private static byte* s_gameUrlUtf8;
    private static char* s_prodUrlWide;
    private static string s_gameUrl = "";
    private static string s_prodUrl = "";
    private static Log? s_log;

    public static bool ApplyGame(HookContext c)
    {
        s_log = c.Log;
        c.Log.Info("==OverrideGameEndpointsData==");
        if (string.IsNullOrEmpty(c.Settings.ServerUrl))
        {
            c.Log.Warn("Server Url is empty or not specified. Skipping!");
            return false;
        }
        if (Urls.Parse(c.Settings.ServerUrl) == null)
        {
            c.Log.Warn($"Server Url \"{c.Settings.ServerUrl}\" does not look like a URL; the game will be pointed at it anyway");
        }

        var hit = c.Patterns.Find("EndpointLoader");
        if (!hit.Found)
        {
            return false;
        }

        nint site = hit.Address + 0x0A;
        nint original = CallSite.Redirect(site, (nint)(delegate* unmanaged<nint, nint, nint>)&OverrideGameEndpoint);
        s_getEndpointKeyValue = (delegate* unmanaged<nint, byte*, nint>)original;
        GameFunctions.Register(GetEndpointKeyValueName, original, FunctionSource.CallSite, $"call at 0x{site:X}", "const char** GetEndpointKeyValue(int64_t* dest, const char* value)", c.Image);

        // The C++ passed the game URL exactly as configured, trailing slash included (its
        // strip was dead code), so that is what production has been running with.
        s_gameUrl = c.Settings.ServerUrl;
        s_gameUrlUtf8 = (byte*)Marshal.StringToCoTaskMemUTF8(s_gameUrl);
        c.Log.Success("EndpointLoader Proxied");
        return true;
    }

    public static bool ApplyProd(HookContext c)
    {
        s_log = c.Log;
        c.Log.Info("==OverrideProdEndpointsData==");
        if (string.IsNullOrEmpty(c.Settings.ProdServerUrl))
        {
            c.Log.Warn("Prod Server Url is empty or not specified. Skipping!");
            return false;
        }
        if (Urls.Parse(c.Settings.ProdServerUrl) == null)
        {
            c.Log.Warn($"Prod Server Url \"{c.Settings.ProdServerUrl}\" does not look like a URL; the game will be pointed at it anyway");
        }

        var hit = c.Patterns.Find("ProdEndpointLoader");
        if (!hit.Found)
        {
            return false;
        }

        nint site = hit.Address + 0x0A;
        nint original = CallSite.Redirect(site, (nint)(delegate* unmanaged<nint, nint, nint>)&OverrideProdEndpoint);
        s_setFStringValue = (delegate* unmanaged<nint, char*, nint>)original;
        GameFunctions.Register(SetFStringValueName, original, FunctionSource.CallSite, $"call at 0x{site:X}", "int64_t* SetFStringValue(int64_t* fstring, const wchar_t* value)", c.Image);

        s_prodUrl = c.Settings.ProdServerUrl.TrimEnd('/') + "/";
        s_prodUrlWide = (char*)Marshal.StringToCoTaskMemUni(s_prodUrl);
        c.Log.Success("ProdEndpointLoader Proxied");
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint OverrideGameEndpoint(nint dest, nint endpoint) =>
        HookGuard.Run("OverrideGameEndpoint", (dest, endpoint), static s =>
        {
            if (s_gameUrlUtf8 != null)
            {
                s_log?.Info($"Rerouting traffic from vanilla HTTP/WS server \"{ReadUtf8(s.endpoint)}\" to \"{s_gameUrl}\"!");
                s_getEndpointKeyValue(s.dest, s_gameUrlUtf8);
                return s.dest;
            }
            return s_getEndpointKeyValue(s.dest, (byte*)s.endpoint);
        }, dest);

    [UnmanagedCallersOnly]
    private static nint OverrideProdEndpoint(nint fstring, nint endpoint) =>
        HookGuard.Run("OverrideProdEndpoint", (fstring, endpoint), static s =>
        {
            if (s_prodUrlWide != null)
            {
                s_log?.Info($"Rerouting traffic from vanilla Prod HTTP/WS server \"{ReadWide(s.endpoint)}\" to \"{s_prodUrl}\"!");
                s_setFStringValue(s.fstring, s_prodUrlWide);
            }
            else
            {
                s_setFStringValue(s.fstring, (char*)s.endpoint);
            }

            return s.fstring;
        }, fstring);

    private static string ReadUtf8(nint p) => p == 0 ? "" : Marshal.PtrToStringUTF8(p) ?? "";
    private static string ReadWide(nint p) => p == 0 ? "" : Marshal.PtrToStringUni(p) ?? "";

}
