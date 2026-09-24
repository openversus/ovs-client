using System.Runtime.InteropServices;

namespace OpenVersus.Native;

public static partial class WinHttp
{
    public const uint WINHTTP_ACCESS_TYPE_DEFAULT_PROXY = 0;
    public const uint WINHTTP_ACCESS_TYPE_NO_PROXY = 1;
    public const uint WINHTTP_FLAG_SECURE = 0x00800000;
    public const uint WINHTTP_QUERY_STATUS_CODE = 19;
    public const uint WINHTTP_QUERY_FLAG_NUMBER = 0x20000000;
    public const uint WINHTTP_OPTION_CONNECT_TIMEOUT = 3;
    public const uint WINHTTP_OPTION_SEND_TIMEOUT = 5;
    public const uint WINHTTP_OPTION_RECEIVE_TIMEOUT = 6;

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint WinHttpOpen(string agent, uint accessType, string? proxy, string? proxyBypass, uint flags);

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint WinHttpConnect(nint session, string host, ushort port, uint reserved);

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint WinHttpOpenRequest(nint connect, string verb, string path, string? version, string? referrer, nint acceptTypes, uint flags);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSetTimeouts(nint handle, int resolve, int connect, int send, int receive);

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSendRequest(nint request, string? headers, uint headersLength, ReadOnlySpan<byte> optional, uint optionalLength, uint totalLength, nuint context);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReceiveResponse(nint request, nint reserved);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpQueryHeaders(nint request, uint infoLevel, nint name, ref uint buffer, ref uint bufferLength, ref uint index);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpQueryDataAvailable(nint request, out uint available);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReadData(nint request, Span<byte> buffer, uint toRead, out uint read);

    [LibraryImport("winhttp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpCloseHandle(nint handle);
}
