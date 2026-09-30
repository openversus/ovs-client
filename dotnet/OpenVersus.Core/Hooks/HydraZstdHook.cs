using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using OpenVersus.Net;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Lets the server send hiss_amalgamation's compressed sections as zstd, which is much smaller than zlib. The game
/// unpacks them in one place: a method of the Hydra SDK's zlib stream class (0x145005170 in the game's only build)
/// that inflates a section chunk by chunk into a sink. Every section of a login goes through it, and nothing else
/// does (checked in the game with a logging probe). Its one call to zlib's inflate is redirected to
/// <see cref="ZstdInflate"/>, which decodes a zstd stream and leaves every other stream to zlib unchanged.
/// </summary>
public static unsafe class HydraZstdHook
{
    /// <summary>The inflate call in the stream method: strm = the object + 0x20, flush 0 (Z_NO_FLUSH).</summary>
    internal const string Pattern = "48 8D 4E 20 89 46 28 33 D2 89 5E 38 48 89 6E 30 E8 ? ? ? ?";
    private const int Call = 16;

    private static delegate* unmanaged<nint, int, int> s_inflate;

    /// <summary>Redirects the inflate call; false when the pattern is missing.</summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==HydraZstd==");
        var site = c.Patterns.Find("HydraInflate", Pattern);
        if (!site.Found)
        {
            return false;
        }

        nint call = site.Address + Call;
        ZstdInflate.Attach(c.Log);
        InflateTiming.Attach(c.Log);
        s_inflate = (delegate* unmanaged<nint, int, int>)CallSite.Destination(call);
        CallSite.Redirect(call, (nint)(delegate* unmanaged<nint, int, int>)&Inflate);
        c.Log.Success($"HydraZstd: zstd sections are decoded at 0x{call:X}; zlib ones go to inflate at 0x{(nint)s_inflate:X}");
        return true;
    }

    [UnmanagedCallersOnly]
    private static int Inflate(nint strm, int flush)
    {
        var z = (ZstdInflate.ZStream*)strm;
        bool first = z->TotalIn == 0;
        long started = Stopwatch.GetTimestamp();
        // A failure inside the decoder fails the section the way corrupt zlib data would.
        int result = HookGuard.Run("HydraZstd", strm, static s => ZstdInflate.Step(s), ZstdInflate.DataError);
        bool zstd = result != ZstdInflate.NotZstd;
        if (!zstd)
        {
            result = s_inflate(strm, flush);
        }

        long ticks = Stopwatch.GetTimestamp() - started;
        HookGuard.Run("HydraZstd.Timing", (strm, first, zstd, result, started, ticks, totalIn: z->TotalIn, totalOut: z->TotalOut),
            static s => InflateTiming.Record(s.strm, s.first, s.zstd, s.result, s.started, s.ticks, s.totalIn, s.totalOut));
        return result;
    }
}
