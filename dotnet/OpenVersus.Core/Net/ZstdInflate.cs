using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ZstdSharp.Unsafe;

namespace OpenVersus.Net;

/// <summary>
/// zlib's inflate for a stream that holds a zstd frame instead: the frame is decoded with ZstdSharp and handed back
/// through the z_stream fields zlib fills (next_in, avail_in, total_in, next_out, avail_out, total_out) with zlib's
/// result codes, so a caller written for zlib cannot tell. Used by <see cref="Hooks.HydraZstdHook"/>.
/// <para>
/// A stream is recognized on its first call (total_in 0) by the zstd frame magic, 28 B5 2F FD. No zlib stream starts
/// with it: its first two bytes, as a big-endian number, are a multiple of 31, and 0x28B5 is not. So the first call
/// must hand over at least 4 bytes (the game's first call on a section hands over at least 8). A stream that is not
/// zstd is left alone (<see cref="NotZstd"/>) for zlib. The decoder's state is kept per z_stream until the
/// frame ends or fails; a z_stream that starts over (total_in 0 again) drops what was left of its last stream.
/// </para>
/// </summary>
public static unsafe class ZstdInflate
{
    /// <summary>What <see cref="Step"/> answers for a stream that is not zstd: zlib's inflate must handle the call.</summary>
    public const int NotZstd = int.MinValue;

    /// <summary>zlib's result codes.</summary>
    public const int Ok = 0, StreamEnd = 1, DataError = -3, BufError = -5;

    // 28 B5 2F FD, read as a little-endian number.
    private const uint Magic = 0xFD2FB528;

    // The largest window a frame may ask for: 16 MB. The server's frames carry their content size, so their window is
    // the section's size (1.5 MB for the largest); zstd's own default limit is 128 MB.
    private const int WindowLogMax = 24;

    /// <summary>zlib's z_stream on Win64 (uLong is 4 bytes there), up to total_out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZStream
    {
        /// <summary>The next input byte.</summary>
        public byte* NextIn;
        /// <summary>How many input bytes are there from <see cref="NextIn"/>.</summary>
        public uint AvailIn;
        /// <summary>How many input bytes the stream has read so far; the game's method reads its input from here.</summary>
        public uint TotalIn;
        /// <summary>Where the next output byte goes.</summary>
        public byte* NextOut;
        /// <summary>How much room is left from <see cref="NextOut"/>; the game hands on what the call filled.</summary>
        public uint AvailOut;
        /// <summary>How many bytes the stream has written so far.</summary>
        public uint TotalOut;
    }

    private static readonly Dictionary<nint, nint> s_streams = new();
    private static readonly object s_lock = new();
    private static ILogger? s_log;
    private static int s_decoded;

    /// <summary>Sets the logger the first decoded frame and every failure are reported to.</summary>
    public static void Attach(ILogger log) => s_log = log;

    /// <summary>How many frames have been decoded to the end.</summary>
    public static int Decoded => Volatile.Read(ref s_decoded);

    /// <summary>
    /// One inflate call on <paramref name="strm"/> (a z_stream): decodes what it can of a zstd stream and answers as
    /// zlib would, or answers <see cref="NotZstd"/>.
    /// </summary>
    public static int Step(nint strm)
    {
        var z = (ZStream*)strm;
        ZSTD_DCtx_s* dctx;
        lock (s_lock)
        {
            if (z->TotalIn == 0)
            {
                Free(strm);
                if (z->AvailIn < sizeof(uint) || Unsafe.ReadUnaligned<uint>(z->NextIn) != Magic)
                {
                    return NotZstd;
                }

                dctx = Methods.ZSTD_createDCtx();
                Methods.ZSTD_DCtx_setParameter(dctx, ZSTD_dParameter.ZSTD_d_windowLogMax, WindowLogMax);
                s_streams[strm] = (nint)dctx;
            }
            else if (s_streams.TryGetValue(strm, out nint found))
            {
                dctx = (ZSTD_DCtx_s*)found;
            }
            else
            {
                return NotZstd;
            }
        }

        var input = new ZSTD_inBuffer_s { src = z->NextIn, size = z->AvailIn, pos = 0 };
        var output = new ZSTD_outBuffer_s { dst = z->NextOut, size = z->AvailOut, pos = 0 };
        nuint hint = Methods.ZSTD_decompressStream(dctx, &output, &input);
        uint read = (uint)input.pos, written = (uint)output.pos;
        z->NextIn += read;
        z->AvailIn -= read;
        z->TotalIn += read;
        z->NextOut += written;
        z->AvailOut -= written;
        z->TotalOut += written;

        if (Methods.ZSTD_isError(hint))
        {
            string error = Methods.ZSTD_getErrorName(hint);
            Drop(strm);
            s_log?.Warn($"[HydraZstd] a zstd section failed after {z->TotalIn} bytes in, {z->TotalOut} out: {error}");
            return DataError;
        }

        if (hint == 0)
        {
            // The frame is decoded and all of it handed out.
            Drop(strm);
            if (Interlocked.Increment(ref s_decoded) == 1)
            {
                s_log?.Info($"[HydraZstd] the first zstd section is decoded: {z->TotalIn} bytes in, {z->TotalOut} out");
            }

            return StreamEnd;
        }

        // zlib's answer when nothing could be read or written (a truncated stream): the caller decides what to do.
        return read == 0 && written == 0 ? BufError : Ok;
    }

    private static void Drop(nint strm)
    {
        lock (s_lock)
        {
            Free(strm);
        }
    }

    // Under s_lock.
    private static void Free(nint strm)
    {
        if (s_streams.Remove(strm, out nint dctx))
        {
            Methods.ZSTD_freeDCtx((ZSTD_DCtx_s*)dctx);
        }
    }
}
