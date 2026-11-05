using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Net;

/// <summary>
/// How long each section at the Hydra inflate call takes (<see cref="Hooks.HydraZstdHook"/>), zstd or zlib alike: the
/// time spent inside the decoder, over how many calls, and the time from its first call to its last (which includes
/// what the game does between calls). One line per section, with the running total, at Debug ([Settings] LogLevel
/// shows it), so a login's cost can be read off the log when wanted; a section that fails is a warning. Measured 2026-09-30 in the game: all 20 hiss sections as zstd take 5-8 ms
/// of decoding per login, the first about 0.3 ms (warming the decoder at startup saved under 0.1 ms, so it is not done).
/// </summary>
public static class InflateTiming
{
    private sealed class Section
    {
        public required bool Zstd;
        public required long First;
        public long Ticks;
        public int Calls;
    }

    private static readonly Dictionary<nint, Section> s_open = new();
    private static readonly object s_lock = new();
    private static ILogger? s_log;
    private static int s_sections;
    private static long s_totalTicks;

    /// <summary>Sets the logger the lines go to.</summary>
    public static void Attach(ILogger log) => s_log = log;

    /// <summary>
    /// One call on <paramref name="strm"/>: <paramref name="first"/> when it started a stream (total_in was 0), which
    /// decoder answered, zlib's result, when it started and how long it took (Stopwatch ticks); the stream's totals
    /// are read from <paramref name="totalIn"/> and <paramref name="totalOut"/> when it ends.
    /// </summary>
    public static void Record(nint strm, bool first, bool zstd, int result, long started, long ticks, uint totalIn, uint totalOut)
    {
        string? line = null;
        bool failed = false;
        lock (s_lock)
        {
            if (first || !s_open.TryGetValue(strm, out var section))
            {
                section = new Section { Zstd = zstd, First = started };
                s_open[strm] = section;
            }

            section.Ticks += ticks;
            section.Calls++;
            if (result is ZstdInflate.Ok or ZstdInflate.BufError)
            {
                return;
            }

            s_open.Remove(strm);
            s_totalTicks += section.Ticks;
            int number = ++s_sections;
            failed = result != ZstdInflate.StreamEnd;
            string outcome = failed ? $", failed ({result})" : "";
            line = $"[HydraZstd] section {number} ({(section.Zstd ? "zstd" : "zlib")}): {totalIn} -> {totalOut} bytes, "
                + $"{Ms(section.Ticks):F2} ms decoding in {section.Calls} calls, {Ms(started + ticks - section.First):F2} ms first call to last; "
                + $"{Ms(s_totalTicks):F2} ms decoding so far{outcome}";
        }

        if (failed)
        {
            s_log?.Warn(line);
        }
        else
        {
            s_log?.Debug(line);
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
