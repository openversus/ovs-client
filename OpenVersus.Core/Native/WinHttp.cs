using System.Runtime.InteropServices;

namespace OpenVersus.Native;

/// <summary>The WinHTTP functions and constants <see cref="Net.WinHttpTransport"/> uses. Names follow the Windows SDK.</summary>
public static partial class WinHttp
{
    /// <summary>WinHttpOpen: use the proxy configured for WinHTTP.</summary>
    public const uint WINHTTP_ACCESS_TYPE_DEFAULT_PROXY = 0;
    /// <summary>WinHttpOpen: connect directly.</summary>
    public const uint WINHTTP_ACCESS_TYPE_NO_PROXY = 1;
    /// <summary>WinHttpOpenRequest: use TLS.</summary>
    public const uint WINHTTP_FLAG_SECURE = 0x00800000;
    /// <summary>WinHttpQueryHeaders: the status code.</summary>
    public const uint WINHTTP_QUERY_STATUS_CODE = 19;
    /// <summary>WinHttpQueryHeaders: return the header as a number.</summary>
    public const uint WINHTTP_QUERY_FLAG_NUMBER = 0x20000000;
    /// <summary>The connect timeout option.</summary>
    public const uint WINHTTP_OPTION_CONNECT_TIMEOUT = 3;
    /// <summary>The send timeout option.</summary>
    public const uint WINHTTP_OPTION_SEND_TIMEOUT = 5;
    /// <summary>The receive timeout option.</summary>
    public const uint WINHTTP_OPTION_RECEIVE_TIMEOUT = 6;

    /// <summary>WinHttpOpen: a session; 0 on failure.</summary>
    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint WinHttpOpen(string agent, uint accessType, string? proxy, string? proxyBypass, uint flags);

    /// <summary>WinHttpConnect: a connection to one host and port; 0 on failure.</summary>
    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint WinHttpConnect(nint session, string host, ushort port, uint reserved);

    /// <summary>WinHttpOpenRequest; 0 on failure.</summary>
    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint WinHttpOpenRequest(nint connect, string verb, string path, string? version, string? referrer, nint acceptTypes, uint flags);

    /// <summary>WinHttpSetTimeouts, in milliseconds.</summary>
    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSetTimeouts(nint handle, int resolve, int connect, int send, int receive);

    /// <summary>WinHttpSendRequest.</summary>
    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSendRequest(nint request, string? headers, uint headersLength, ReadOnlySpan<byte> optional, uint optionalLength, uint totalLength, nuint context);

    /// <summary>WinHttpReceiveResponse: waits for the response headers.</summary>
    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReceiveResponse(nint request, nint reserved);

    /// <summary>WinHttpQueryHeaders, here for one numeric header.</summary>
    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpQueryHeaders(nint request, uint infoLevel, nint name, ref uint buffer, ref uint bufferLength, ref uint index);

    /// <summary>WinHttpQueryDataAvailable: 0 available means the body is complete.</summary>
    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpQueryDataAvailable(nint request, out uint available);

    /// <summary>WinHttpReadData.</summary>
    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReadData(nint request, Span<byte> buffer, uint toRead, out uint read);

    /// <summary>WinHttpCloseHandle.</summary>
    [LibraryImport("winhttp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpCloseHandle(nint handle);
}
