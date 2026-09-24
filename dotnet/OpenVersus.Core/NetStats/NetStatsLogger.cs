using System.Diagnostics;
using OpenVersus.Game;
using OpenVersus.Memory;
using Microsoft.Extensions.Logging;

namespace OpenVersus.NetStats;

/// <summary>
/// Turns "the match felt choppy" into numbers. While a netcode session is in the playing
/// state, the session's own counters are sampled at 60 Hz through guarded reads and one
/// NETSTATS line per second goes to <paramref name="stats"/>, its own log file, so the main log
/// is not buried under them; when the match ends locally, a summary goes to both. See
/// HANDOFF-netstats. Opt-in through [Features] NetStats. <paramref name="findInterval"/> is how
/// often the object walk that looks for a session runs while there is none (a full walk each
/// time, so seconds apart in the game; milliseconds in a test). Nothing is asked of the engine
/// until <see cref="Engine.IsUp"/>; asking for a name before that took the game down (2026-09-24).
/// </summary>
public sealed class NetStatsLogger(ObjectFinder finder, ILogger log, ILogger stats, TimeSpan? findInterval = null)
{
    private readonly long _findIntervalMs = (long)(findInterval ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;

    // Lives as long as the process; never disposed, since the loop thread may be waiting on it.
    private readonly CancellationTokenSource _stopping = new();

    public void Start()
    {
        CancellationToken token = _stopping.Token;
        new Thread(() => Loop(token)) { IsBackground = true, Name = "OVS netstats" }.Start();
        log.Info("[NetStats] enabled; sampling at 60 Hz while a session is playing, lines in the NetStats log");
    }

    public void Stop() => _stopping.Cancel();

    /// <summary>The counters for one session, from the first playing sample to the summary.</summary>
    private sealed class Match(nint session)
    {
        public nint Session { get; } = session;
        public int StartFrame { get; set; }
        public int LastFrame { get; set; }
        public int LastResim { get; set; }
        public int StartResim { get; set; }
        public int Rollbacks { get; set; }
        public int MaxDepth { get; set; }
        public int Rollbacks1s { get; set; }
        public int MaxDepth1s { get; set; }
        public int ResimAtSecond { get; set; }
        public double DepthSum { get; set; }
        public bool Summarized { get; set; }
    }

    private void Loop(CancellationToken stopping)
    {
        // 5.714286 × 0.7 = 4.0

        log.Info("[NetStats] waiting for the game to start");
        if (!Engine.WaitUntilUp(stopping))
        {
            return;
        }

        log.Info("[NetStats] game up; looking for a netcode session");
        nint sessionClass = 0;
        Match? match = null;
        var clock = Stopwatch.StartNew();
        long nextFind = 0, nextLine = 0;
        while (!stopping.IsCancellationRequested)
        {
            long now = clock.ElapsedMilliseconds;
            try
            {
                if (sessionClass == 0 && finder.Names.Ready && now >= nextFind)
                {
                    sessionClass = finder.FindClass("PfgNetcodeSession");
                    nextFind = now + _findIntervalMs;
                }
                // A full object walk each time, so not more often than the interval while idle.
                if (match == null && sessionClass != 0 && now >= nextFind)
                {
                    nextFind = now + _findIntervalMs;
                    nint session = finder.FindInstanceOfClass(sessionClass);
                    if (session != 0)
                    {
                        match = new Match(session);
                        nextLine = now + 1000;
                        log.Info($"[NetStats] session 0x{session:X}");
                    }
                }
                if (match != null)
                {
                    if (!Sample(match, out bool gone) && gone)
                    {
                        log.Info("[NetStats] session gone");
                        match = null;
                    }
                    else if (now >= nextLine)
                    {
                        nextLine = now + 1000;
                        Line(match);
                    }
                }
            }
            catch (Exception e)
            {
                log.Error($"[NetStats] {e.Message}");
                match = null;
            }
            stopping.WaitHandle.WaitOne(16);
        }
    }

    private bool Sample(Match m, out bool gone)
    {
        gone = false;
        if (!ObjectHeader.TryRead(finder.Memory, m.Session, out var h) || !finder.Memory.TryRead(m.Session + Mvs.SessionState, out byte state))
        {
            gone = true;
            return false;
        }
        if (state == 4)
        {
            finder.Memory.TryRead(m.Session + Mvs.SessionResimCount, out int resim);
            if (m.StartFrame == 0)
            {
                m.StartFrame = Frame(m.Session);
                m.StartResim = m.LastResim = m.ResimAtSecond = resim;
            }
            if (resim > m.LastResim)
            {
                int depth = resim - m.LastResim;
                m.Rollbacks++;
                m.Rollbacks1s++;
                m.DepthSum += depth;
                if (depth > m.MaxDepth)
                {
                    m.MaxDepth = depth;
                }

                if (depth > m.MaxDepth1s)
                {
                    m.MaxDepth1s = depth;
                }
            }
            m.LastResim = resim;
            m.LastFrame = Frame(m.Session);
            return true;
        }
        if (state is 7 or 8 && !m.Summarized && m.StartFrame != 0)
        {
            m.Summarized = true;
            int frames = m.LastFrame - m.StartFrame, total = m.LastResim - m.StartResim;
            finder.Memory.TryRead(m.Session + Mvs.SessionInputDelay, out int delay);
            string summary = $"NETSTATS-SUMMARY frames={frames} resim_total={total} rollbacks={m.Rollbacks} mean_depth={(m.Rollbacks > 0 ? m.DepthSum / m.Rollbacks : 0):F2} max_depth={m.MaxDepth} final_delay={delay} state={state}";
            stats.Info(summary);
            log.Info(summary);
        }
        if (state is 6 or 9)
        {
            gone = true;
        }

        return false;
    }

    private void Line(Match m)
    {
        nint s = m.Session;
        finder.Memory.TryRead(s + Mvs.SessionState, out byte state);
        if (state != 4)
        {
            return;
        }

        finder.Memory.TryRead(s + Mvs.SessionInputDelay, out int delay);
        finder.Memory.TryRead(s + Mvs.SessionPing, out int ping);
        finder.Memory.TryRead(s + Mvs.SessionRift, out float rift);
        finder.Memory.TryRead(s + Mvs.SessionNumPredictedOverrides, out int pred);
        finder.Memory.TryRead(s + Mvs.SessionNumZeroedOverrides, out int zero);
        finder.Memory.TryRead(s + Mvs.SessionPlayerIndex, out int player);
        int resim1s = m.LastResim - m.ResimAtSecond;
        stats.Info($"NETSTATS player={player} frame={m.LastFrame} delay={delay} ping={ping} rift={rift:F2} resim_total={m.LastResim - m.StartResim} resim_1s={resim1s} rollbacks_1s={m.Rollbacks1s} max_depth_1s={m.MaxDepth1s} pred={pred} zero={zero}");
        m.ResimAtSecond = m.LastResim;
        m.Rollbacks1s = 0;
        m.MaxDepth1s = 0;
    }

    private int Frame(nint session) =>
        finder.Memory.TryRead(session + Mvs.SessionStateManager, out nint manager) && manager != 0 && finder.Memory.TryRead(manager + Mvs.StateManagerCurrentFrame, out int frame) ? frame : 0;
}
