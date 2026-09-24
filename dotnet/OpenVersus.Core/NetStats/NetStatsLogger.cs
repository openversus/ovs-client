using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenVersus.Game;
using OpenVersus.Memory;

namespace OpenVersus.NetStats;

/// <summary>
/// What a match log is named for: the match id, the local player's teammates and the opponents,
/// as far as they could be read. A 1v1 has one opponent; a 2v2 one teammate and two opponents; a
/// free-for-all puts every player on a team of their own, so all are opponents.
/// </summary>
public sealed record MatchInfo(string MatchId, IReadOnlyList<string> Teammates, IReadOnlyList<string> Opponents)
{
    /// <summary>"_&lt;match id&gt;[_with_&lt;teammates&gt;]_vs_&lt;opponents&gt;", each part only if known and made safe for a file name.</summary>
    public string FileSuffix
    {
        get
        {
            string id = GameStrings.ForFileName(MatchId, 40);
            string mates = Names(Teammates);
            string names = Names(Opponents);
            return (id.Length > 0 ? "_" + id : "") + (mates.Length > 0 ? "_with_" + mates : "") + (names.Length > 0 ? "_vs_" + names : "");

            // Each name made safe on its own, then joined with '+', which the sanitizer would otherwise eat.
            static string Names(IReadOnlyList<string> people) =>
                string.Join("+", people.Select(p => GameStrings.ForFileName(p, 24)).Where(p => p.Length > 0));
        }
    }

    public override string ToString() =>
        $"match {(MatchId.Length > 0 ? MatchId : "?")}{(Teammates.Count > 0 ? " with " + string.Join(", ", Teammates) : "")} vs {(Opponents.Count > 0 ? string.Join(", ", Opponents) : "?")}";
}

/// <summary>
/// Turns "the match felt choppy" into numbers. While a netcode session is in the playing
/// state, the session's own counters are sampled at 60 Hz through guarded reads and one
/// NETSTATS line per second goes to a log opened for that match through
/// <paramref name="openMatchLog"/>; when the match ends locally, a summary goes there and to
/// the main log, and the match log is closed (disposed), which is what archives it under the
/// match's start time. The next match gets a fresh one. See HANDOFF-netstats. Opt-in through
/// [Features] NetStats. <paramref name="findInterval"/> is how often the object walk that looks
/// for a session runs while there is none (a full walk each time, so seconds apart in the game;
/// milliseconds in a test). Nothing is asked of the engine until <see cref="Engine.IsUp"/>;
/// asking for a name before that took the game down (2026-09-24).
/// </summary>
public sealed class NetStatsLogger(ObjectFinder finder, ILogger log, Func<MatchInfo, ILogger> openMatchLog, TimeSpan? findInterval = null)
{
    private readonly long _findIntervalMs = (long)(findInterval ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;

    // Lives as long as the process; never disposed, since the loop thread may be waiting on it.
    private readonly CancellationTokenSource _stopping = new();

    public void Start()
    {
        CancellationToken token = _stopping.Token;
        new Thread(() => Loop(token)) { IsBackground = true, Name = "OVS netstats" }.Start();
        log.Info("[NetStats] enabled; sampling at 60 Hz while a session is playing, one NetStats log per match");
    }

    public void Stop() => _stopping.Cancel();

    /// <summary>The counters for one session. A match starts at the first playing sample and ends at the summary.</summary>
    private sealed class Match(nint session)
    {
        public nint Session { get; } = session;
        /// <summary>The log for the match in progress; null between matches.</summary>
        public ILogger? Stats { get; set; }
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
                        EndMatch(match, "session gone before the match ended");
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
                if (match != null)
                {
                    EndMatch(match, "sampler failed");
                    match = null;
                }
            }
            stopping.WaitHandle.WaitOne(16);
        }

        if (match != null)
        {
            EndMatch(match, "netstats stopped");
        }
    }

    /// <summary>
    /// One sample. Playing (state 4) opens the match log on its first sample and counts
    /// rollbacks; ended locally (7 or 8) writes the summary once and closes the match log;
    /// failed or disconnecting (6 or 9), or an unreadable session, reports the session gone.
    /// </summary>
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
            if (m.Stats == null)
            {
                BeginMatch(m);
            }

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
        if (state is 7 or 8 && m.Stats != null && m.StartFrame != 0)
        {
            int frames = m.LastFrame - m.StartFrame, total = m.LastResim - m.StartResim;
            finder.Memory.TryRead(m.Session + Mvs.SessionInputDelay, out int delay);
            string summary = $"NETSTATS-SUMMARY frames={frames} resim_total={total} rollbacks={m.Rollbacks} mean_depth={(m.Rollbacks > 0 ? m.DepthSum / m.Rollbacks : 0):F2} max_depth={m.MaxDepth} final_delay={delay} state={state}";
            m.Stats.Info(summary);
            log.Info(summary);
            EndMatch(m, "match ended");
        }
        if (state is 6 or 9)
        {
            gone = true;
        }

        return false;
    }

    /// <summary>A fresh log and fresh counters: the session may host more than one match.</summary>
    private void BeginMatch(Match m)
    {
        MatchInfo info = DescribeMatch(m.Session);
        m.Stats = openMatchLog(info);
        m.StartFrame = m.LastFrame = m.LastResim = m.StartResim = 0;
        m.Rollbacks = m.MaxDepth = m.Rollbacks1s = m.MaxDepth1s = m.ResimAtSecond = 0;
        m.DepthSum = 0;
        log.Info($"[NetStats] match started on session 0x{m.Session:X} ({info}); match log opened");
    }

    /// <summary>
    /// The match id and the other players' names, for the log's name. The pawns in play carry
    /// their player states; the UMatchPlayerData_C objects of this match are the ones whose
    /// player state is one of those (stale ones from earlier matches are not), and they carry
    /// the username and the match id. Anything unreadable leaves the field empty.
    /// </summary>
    private MatchInfo DescribeMatch(nint session)
    {
        try
        {
            finder.Memory.TryRead(session + Mvs.SessionPlayerIndex, out int localIndex);
            var playerStates = new HashSet<nint>();
            foreach (nint pawn in finder.FindInstancesOfClass(finder.FindClass("PfgFixedPawn")))
            {
                if (finder.Memory.TryRead(pawn + Mvs.PawnPlayerState, out nint state) && state != 0)
                {
                    playerStates.Add(state);
                }
            }

            // This match's players: index, team and name, in player-index order.
            string matchId = "";
            var players = new List<(int Index, int Team, string Name)>();
            foreach (nint data in finder.FindInstancesOfClass(finder.FindClass("MatchPlayerData_C")))
            {
                if (!finder.Memory.TryRead(data + Mvs.MatchPlayerDataPlayerState, out nint state) || !playerStates.Contains(state))
                {
                    continue;
                }

                finder.Memory.TryRead(data + Mvs.MatchPlayerDataPlayerIndex, out int index);
                finder.Memory.TryRead(data + Mvs.MatchPlayerDataTeamIndex, out int team);
                if (matchId.Length == 0 && GameStrings.TryReadFString(finder.Memory, data + Mvs.MatchPlayerDataMatchId, out string id))
                {
                    matchId = id;
                }

                if (GameStrings.TryReadFString(finder.Memory, data + Mvs.MatchPlayerDataUsername, out string name) && name.Length > 0)
                {
                    players.Add((index, team, name));
                }
            }

            players.Sort((a, b) => a.Index.CompareTo(b.Index));
            int localTeam = int.MinValue;
            foreach (var player in players)
            {
                if (player.Index == localIndex)
                {
                    localTeam = player.Team;
                    break;
                }
            }

            var teammates = players.Where(p => p.Index != localIndex && p.Team == localTeam).Select(p => p.Name).ToList();
            var opponents = players.Where(p => p.Index != localIndex && p.Team != localTeam).Select(p => p.Name).ToList();
            return new MatchInfo(matchId, teammates, opponents);
        }
        catch (Exception e)
        {
            log.Warn($"[NetStats] could not describe the match: {e.Message}");
            return new MatchInfo("", [], []);
        }
    }

    /// <summary>Closes the match log, which archives it under the match's start time.</summary>
    private void EndMatch(Match m, string why)
    {
        if (m.Stats == null)
        {
            return;
        }

        log.Info($"[NetStats] match log closed ({why})");
        (m.Stats as IDisposable)?.Dispose();
        m.Stats = null;
    }

    private void Line(Match m)
    {
        nint s = m.Session;
        finder.Memory.TryRead(s + Mvs.SessionState, out byte state);
        if (state != 4 || m.Stats == null)
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
        m.Stats.Info($"NETSTATS player={player} frame={m.LastFrame} delay={delay} ping={ping} rift={rift:F2} resim_total={m.LastResim - m.StartResim} resim_1s={resim1s} rollbacks_1s={m.Rollbacks1s} max_depth_1s={m.MaxDepth1s} pred={pred} zero={zero}");
        m.ResimAtSecond = m.LastResim;
        m.Rollbacks1s = 0;
        m.MaxDepth1s = 0;
    }

    private int Frame(nint session) =>
        finder.Memory.TryRead(session + Mvs.SessionStateManager, out nint manager) && manager != 0 && finder.Memory.TryRead(manager + Mvs.StateManagerCurrentFrame, out int frame) ? frame : 0;
}
