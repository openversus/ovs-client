using System.Runtime.InteropServices;
using OpenVersus.Native;

namespace OpenVersus.Net;

public sealed unsafe class WinHttpTransport(string agent = "OVS/1.0", bool useSystemProxy = false) : IHttpTransport
{
    public HttpResult Get(Uri url, TimeSpan timeout) => Send(url, "GET", null, default, timeout);

    public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout) =>
        Send(url, "POST", $"Content-Type: {contentType}\r\n", body, timeout);

    private HttpResult Send(Uri url, string verb, string? headers, ReadOnlySpan<byte> body, TimeSpan timeout)
    {
        nint session = 0, connect = 0, request = 0;
        try
        {
            session = WinHttp.WinHttpOpen(agent, useSystemProxy ? WinHttp.WINHTTP_ACCESS_TYPE_DEFAULT_PROXY : WinHttp.WINHTTP_ACCESS_TYPE_NO_PROXY, null, null, 0);
            if (session == 0)
            {
                return HttpResult.Failed($"WinHttpOpen failed ({Marshal.GetLastPInvokeError()})");
            }

            int ms = (int)timeout.TotalMilliseconds;
            WinHttp.WinHttpSetTimeouts(session, ms, ms, ms, ms);

            connect = WinHttp.WinHttpConnect(session, url.Host, (ushort)url.Port, 0);
            if (connect == 0)
            {
                return HttpResult.Failed($"WinHttpConnect failed ({Marshal.GetLastPInvokeError()})");
            }

            bool https = url.Scheme == "https";
            request = WinHttp.WinHttpOpenRequest(connect, verb, url.PathAndQuery, null, null, 0, https ? WinHttp.WINHTTP_FLAG_SECURE : 0);
            if (request == 0)
            {
                return HttpResult.Failed($"WinHttpOpenRequest failed ({Marshal.GetLastPInvokeError()})");
            }

            bool sent;
            fixed (byte* p = body)
            {
                sent = WinHttp.WinHttpSendRequest(request, headers, headers == null ? 0 : uint.MaxValue, body.Length == 0 ? null : p, (uint)body.Length, (uint)body.Length, 0);
            }

            if (!sent)
            {
                return HttpResult.Failed($"WinHttpSendRequest failed ({Marshal.GetLastPInvokeError()})");
            }

            if (!WinHttp.WinHttpReceiveResponse(request, 0))
            {
                return HttpResult.Failed($"WinHttpReceiveResponse failed ({Marshal.GetLastPInvokeError()})");
            }

            uint status = 0, size = sizeof(uint), index = 0;
            WinHttp.WinHttpQueryHeaders(request, WinHttp.WINHTTP_QUERY_STATUS_CODE | WinHttp.WINHTTP_QUERY_FLAG_NUMBER, 0, &status, ref size, ref index);

            var data = new MemoryStream();
            while (WinHttp.WinHttpQueryDataAvailable(request, out uint available) && available > 0)
            {
                var chunk = new byte[available];
                fixed (byte* p = chunk)
                {
                    if (!WinHttp.WinHttpReadData(request, p, available, out uint read) || read == 0)
                    {
                        break;
                    }

                    data.Write(chunk, 0, (int)read);
                }
            }
            return new HttpResult(status is >= 200 and < 300, (int)status, data.ToArray(), null);
        }
        finally
        {
            if (request != 0)
            {
                WinHttp.WinHttpCloseHandle(request);
            }

            if (connect != 0)
            {
                WinHttp.WinHttpCloseHandle(connect);
            }

            if (session != 0)
            {
                WinHttp.WinHttpCloseHandle(session);
            }
        }
    }
}
