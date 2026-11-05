using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using OpenVersus.Hooks;
using OpenVersus.Memory;
using OpenVersus.Net;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="ZstdInflate"/>, driven the way the game's Hydra stream method drives zlib's inflate (0x145005170): per
/// call, next_in = the input + total_in, avail_in = what is left up to the chunk size, and a fresh output buffer of
/// the chunk size; the output is what the call wrote; Z_OK and Z_BUF_ERROR go on, Z_STREAM_END ends the section, and
/// anything else fails it. The frames are made as the server makes them: ZstdSharp, level 22, content size and
/// checksum in the frame. OVS_TEST_HISS_SECTIONS (a directory of the 20 hiss sections as *.plain files) adds the real
/// data.
/// </summary>
public sealed unsafe class ZstdInflateTests
{
    // From 4 bytes up: the stream is recognized by its first call's first 4 bytes (see ZstdInflate). In the game every
    // section's first call had at least 8 (the logging probe printed 8 bytes of each, the 10-byte section's too).
    private static readonly int[] s_chunks = [4, 7, 4096, 65536, 1 << 20];

    private sealed record Run(int Result, byte[] Output, bool Zlib);

    private static byte[] Zstd(byte[] plain)
    {
        using var compressor = new Compressor(22);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor.Wrap(plain).ToArray();
    }

    // One section through the method's loop. Stops on Z_STREAM_END, on an error, when the stream is left to zlib, when
    // two calls in a row neither advance total_in nor write anything (zlib's Z_BUF_ERROR on a truncated stream, or a
    // decoder that goes in circles, whatever it answers), or, given the bytes it should give,
    // as soon as the output stops matching them or grows past them (a decoder that went wrong or never ends:
    // Diverged; without this a decoder that starts over each call would run for hours at a 4-byte chunk).
    private const int Diverged = int.MaxValue;

    private static Run Drive(byte[] input, int chunk, nint strm = 0, byte[]? expected = null)
    {
        bool own = strm == 0;
        if (own)
        {
            strm = (nint)NativeMemory.AllocZeroed((nuint)sizeof(ZstdInflate.ZStream));
        }

        var z = (ZstdInflate.ZStream*)strm;
        *z = default;
        var output = new MemoryStream();
        var buffer = new byte[chunk];
        int stuck = 0;
        try
        {
            fixed (byte* data = input)
            fixed (byte* outBuffer = buffer)
            {
                while (true)
                {
                    uint before = z->TotalIn;
                    z->NextIn = data + z->TotalIn;
                    z->AvailIn = (uint)Math.Min(input.Length - (int)z->TotalIn, chunk);
                    z->NextOut = outBuffer;
                    z->AvailOut = (uint)chunk;
                    int result = ZstdInflate.Step(strm);
                    if (result == ZstdInflate.NotZstd)
                    {
                        return new Run(result, output.ToArray(), true);
                    }

                    int written = chunk - (int)z->AvailOut;
                    if (expected is not null && (output.Length + written > expected.Length
                        || !buffer.AsSpan(0, written).SequenceEqual(expected.AsSpan((int)output.Length, written))))
                    {
                        return new Run(Diverged, output.ToArray(), false);
                    }

                    output.Write(buffer, 0, written);

                    if (result is not (ZstdInflate.Ok or ZstdInflate.BufError))
                    {
                        return new Run(result, output.ToArray(), false);
                    }

                    stuck = z->TotalIn == before && written == 0 ? stuck + 1 : 0;
                    if (stuck == 2)
                    {
                        return new Run(result, output.ToArray(), false);
                    }
                }
            }
        }
        finally
        {
            if (own)
            {
                NativeMemory.Free((void*)strm);
            }
        }
    }

    private static byte[] Json(int bytes)
    {
        var text = new StringBuilder();
        for (int i = 0; text.Length < bytes; i++)
        {
            text.Append($"{{\"slug\":\"skin_{i % 431}_s0{i % 7}\",\"Weight\":{i * 37 % 1000},\"Enabled\":{(i % 3 == 0 ? "true" : "false")}}},");
        }

        return Encoding.UTF8.GetBytes(text.ToString(0, bytes));
    }

    public static TheoryData<string, int> Payloads()
    {
        var data = new TheoryData<string, int>();
        foreach (int chunk in s_chunks)
        {
            data.Add("json 1.5 MB", chunk);
            data.Add("random 100 KB", chunk);
            data.Add("2 bytes", chunk);
        }

        return data;
    }

    private static byte[] Payload(string name) => name switch
    {
        "json 1.5 MB" => Json(1_563_088),
        "random 100 KB" => RandomBytes(100_000),
        _ => [0x60, 0x00],
    };

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void AFrameComesOutWhole(string payload, int chunk)
    {
        byte[] plain = Payload(payload);

        var run = Drive(Zstd(plain), chunk, expected: plain);

        Assert.Equal(ZstdInflate.StreamEnd, run.Result);
        Assert.Equal(plain, run.Output);
    }

    [SkippableFact]
    public void TheHissSectionsComeOutWhole()
    {
        string? dir = Environment.GetEnvironmentVariable("OVS_TEST_HISS_SECTIONS");
        Skip.If(string.IsNullOrEmpty(dir), "OVS_TEST_HISS_SECTIONS not set");
        var sections = Directory.GetFiles(dir!, "*.plain");
        Assert.Equal(20, sections.Length);
        foreach (string file in sections)
        {
            byte[] plain = File.ReadAllBytes(file);
            foreach (int chunk in (int[])[4096, 65536])
            {
                var run = Drive(Zstd(plain), chunk, expected: plain);
                Assert.Equal(ZstdInflate.StreamEnd, run.Result);
                Assert.True(plain.AsSpan().SequenceEqual(run.Output), $"{Path.GetFileName(file)} at chunk {chunk}");
            }
        }
    }

    [Fact]
    public void ZlibIsLeftToZlib()
    {
        byte[] plain = Json(10_000);
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain);
        }

        var run = Drive(buffer.ToArray(), 4096);

        Assert.True(run.Zlib);
        Assert.Empty(run.Output);
    }

    // What a caller loop at the next section sees: the same z_stream, one section after another (the game reuses one
    // object for all 20), and a zlib section after a zstd one.
    [Fact]
    public void OneStreamCarriesSectionAfterSection()
    {
        var strm = (nint)NativeMemory.AllocZeroed((nuint)sizeof(ZstdInflate.ZStream));
        try
        {
            byte[] first = Json(200_000), second = RandomBytes(5_000);
            Assert.Equal(first, Drive(Zstd(first), 4096, strm, first).Output);
            Assert.Equal(second, Drive(Zstd(second), 4096, strm, second).Output);
            // A section abandoned half way, then a zlib one: the zstd state is dropped, zlib gets the call.
            byte[] frame = Zstd(first);
            Assert.Equal(ZstdInflate.BufError, Drive(frame[..(frame.Length / 2)], 4096, strm, first).Result);
            Assert.True(Drive([0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01], 4096, strm).Zlib);
        }
        finally
        {
            NativeMemory.Free((void*)strm);
        }
    }

    [Fact]
    public void ATruncatedFrameGetsNowhereAsZlibWould()
    {
        byte[] plain = Json(300_000), frame = Zstd(plain);

        var run = Drive(frame[..^100], 4096, expected: plain);

        Assert.Equal(ZstdInflate.BufError, run.Result);
        Assert.True(run.Output.Length < plain.Length);
    }

    [Fact]
    public void ACorruptFrameFails()
    {
        byte[] plain = Json(300_000), frame = Zstd(plain);
        frame[frame.Length / 2] ^= 0x40;

        // Garbage comes out before the checksum at the end fails it, so no expected bytes here.
        Assert.Equal(ZstdInflate.DataError, Drive(frame, 4096).Result);
    }

    [Fact]
    public void TheMagicAloneIsNotAFrame()
    {
        // A whole frame header (descriptor, window descriptor) with the descriptor's reserved bit set.
        Assert.Equal(ZstdInflate.DataError, Drive([0x28, 0xB5, 0x2F, 0xFD, 0x08, 0x00, 0x01, 0x00, 0x00], 4096).Result);
    }

    [Fact]
    public void AFrameAskingForAHugeWindowIsRefused()
    {
        // A frame without its content size whose header asks for a 32 MB window (window log 10 + 15): within zstd's
        // own limit (128 MB), over this one (16 MB).
        byte[] frame = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 15 << 3, 0x01, 0x00, 0x00];

        Assert.Equal(ZstdInflate.DataError, Drive(frame, 4096).Result);
    }

    // No zlib stream can start with the zstd magic: a zlib header is a multiple of 31 as a big-endian number.
    [Fact]
    public void NoZlibHeaderLooksLikeTheMagic()
    {
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16BigEndian([0x28, 0xB5]) % 31);
    }

    // One line per finished section (none for the calls in the middle), with the codec, the sizes and the running total.
    [Fact]
    public void EachSectionGetsOneTimingLine()
    {
        var log = new ListLogger();
        InflateTiming.Attach(log);
        long tick = System.Diagnostics.Stopwatch.Frequency / 1000;
        try
        {
            InflateTiming.Record(0x1000, first: true, zstd: true, ZstdInflate.Ok, started: 0, ticks: 2 * tick, 100, 400);
            InflateTiming.Record(0x1000, first: false, zstd: true, ZstdInflate.StreamEnd, started: 5 * tick, ticks: 1 * tick, 150, 900);
            InflateTiming.Record(0x1000, first: true, zstd: false, ZstdInflate.StreamEnd, started: 10 * tick, ticks: 4 * tick, 60, 94);
        }
        finally
        {
            InflateTiming.Attach(new ListLogger());
        }

        var lines = log.Lines.Where(l => l.Contains("[HydraZstd] section", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("(zstd): 150 -> 900 bytes, 3.00 ms decoding in 2 calls, 6.00 ms first call to last", lines[0]);
        Assert.Contains("(zlib): 60 -> 94 bytes, 4.00 ms decoding in 1 calls", lines[1]);
        Assert.Contains("7.00 ms decoding so far", lines[1]);
    }

    [SkippableFact]
    public void ThePatternIsTheOneInflateCallInTheFinalBuild()
    {
        string exe = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe");
        Skip.If(!File.Exists(exe), "game exe not installed here");
        byte[] mapped = PeImage.MapFile(File.ReadAllBytes(exe));

        var hits = PatternScanner.FindAll(mapped, BytePattern.Parse(HydraZstdHook.Pattern));

        int call = Assert.Single(hits) + 16;
        Assert.Equal(0x50051EC, call);
        Assert.Equal(0xE8, mapped[call]);
        Assert.Equal(0x55444B0, call + 5 + BinaryPrimitives.ReadInt32LittleEndian(mapped.AsSpan(call + 1)));
    }
}
