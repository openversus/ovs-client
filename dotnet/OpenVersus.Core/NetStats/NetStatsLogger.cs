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
    /// <summary>Nothing could be read: no id and nobody else.</summary>
    public bool IsEmpty => MatchId.Length == 0 && Teammates.Count == 0 && Opponents.Count == 0;

    /// <summary>An id and at least one opponent, which every match has; less than this is read again.</summary>
    public bool IsComplete => MatchId.Length > 0 && Opponents.Count > 0;

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
/// [Features] NetStats.
/// <para>
/// The session object is found through the object array, and the array stays the authority on
/// whether it is still alive: every sample first asks the array whether the object is still in
/// its slot (<see cref="ObjectFinder.IsLive"/>, two reads), and while no match is playing the
/// lookup repeats every <paramref name="findInterval"/> (a full object walk each time, so
/// seconds apart in the game; milliseconds in a test) so the next match's session is found
/// wherever it is. Holding on to the address instead sampled a freed session whose memory the
/// game had reused, and a second "match" of garbage was logged (2026-09-24).
/// </para>
/// <para>
/// The match's id and players are read for the log's name at the first playing sample; while
/// the reading is incomplete it is repeated once a second for a few seconds, and
/// <paramref name="nameMatchLog"/> is called with each reading that has anything, so "not
/// there yet" and "never there" can be told apart in the log. Nothing is asked of the engine until
/// <see cref="Engine.IsUp"/>; asking for a name before that took the game down (2026-09-24).
/// </para>
/// </summary>
public sealed class NetStatsLogger(ObjectFinder finder, ILogger log, Func<MatchInfo, ILogger> openMatchLog, Action<ILogger, MatchInfo>? nameMatchLog = null, TimeSpan? findInterval = null)
{
    /// <summary>Readings of the match description before giving up on naming the log: the first, then one per second.</summary>
    public const int MaxDescribeAttempts = 10;

    /// <summary>Objects listed per kind in the debug detail of a failed description.</summary>
    private const int DetailCap = 12;

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
        public int DescribeAttempts { get; set; }
        public bool Described { get; set; }
    }

    private enum Sampled
    {
        /// <summary>Not playing: in the lobby, or lingering after a match.</summary>
        Idle,
        Playing,
        /// <summary>No longer in the object array, unreadable, or failed/disconnecting.</summary>
        Gone,
    }

    private void Loop(CancellationToken stopping)
    {
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
                    if (sessionClass == 0)
                    {
                        nextFind = now + _findIntervalMs;
                    }
                }
                // Between matches the object array decides which session is live: a full walk
                // each time, so not more often than the interval, and never during a match. An
                // ended session can stay listed until the garbage collector runs while the next
                // match's session already exists, so a playing one wins over the one held.
                if (sessionClass != 0 && (match == null || match.Stats == null) && now >= nextFind)
                {
                    nextFind = now + _findIntervalMs;
                    nint held = match?.Session ?? 0;
                    nint session = ChooseSession(sessionClass, held, out int listed, out bool heldListed);
                    if (session != held)
                    {
                        if (held != 0)
                        {
                            log.Info(heldListed
                                ? $"[NetStats] session 0x{session:X} is playing while 0x{held:X} is still listed; switching"
                                : $"[NetStats] session 0x{held:X} is no longer in the object array");
                            match = null;
                        }
                        if (session != 0)
                        {
                            match = new Match(session);
                            nextLine = now + 1000;
                            log.Info($"[NetStats] session 0x{session:X}{(listed > 1 ? $" ({listed} listed)" : "")}");
                        }
                    }
                    else if (listed > 1)
                    {
                        log.Debug($"[NetStats] {listed} sessions listed, none playing; keeping 0x{held:X}");
                    }
                }
                if (match != null)
                {
                    switch (Sample(match))
                    {
                        case Sampled.Gone:
                            log.Info("[NetStats] session gone");
                            EndMatch(match, "session gone before the match ended");
                            match = null;
                            nextFind = now;
                            break;
                        case Sampled.Playing:
                            if (now >= nextLine)
                            {
                                nextLine = now + 1000;
                                if (!match.Described && match.DescribeAttempts < MaxDescribeAttempts)
                                {
                                    Describe(match);
                                }

                                Line(match);
                            }
                            break;
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
    /// The session to sample among those the object array lists: one that is playing, else the
    /// one already held if it is still listed, else the first. <paramref name="listed"/> is how
    /// many there were and <paramref name="heldListed"/> whether the held one was among them.
    /// </summary>
    private nint ChooseSession(nint sessionClass, nint held, out int listed, out bool heldListed)
    {
        listed = 0;
        heldListed = false;
        nint first = 0, playing = 0;
        foreach (nint session in finder.FindInstancesOfClass(sessionClass))
        {
            listed++;
            if (first == 0)
            {
                first = session;
            }

            if (session == held)
            {
                heldListed = true;
            }

            if (playing == 0 && finder.Memory.TryRead(session + Mvs.SessionState, out byte state) && state == 4)
            {
                playing = session;
            }
        }

        return playing != 0 ? playing : heldListed ? held : first;
    }

    /// <summary>
    /// One sample. Playing (state 4) opens the match log on its first sample and counts
    /// rollbacks; ended locally (7 or 8) writes the summary once and closes the match log, and
    /// the session is kept for the next match while the array lists it; failed or disconnecting
    /// (6 or 9), or a session the object array no longer lists, reports the session gone.
    /// </summary>
    private Sampled Sample(Match m)
    {
        if (!finder.IsLive(m.Session) || !ObjectHeader.TryRead(finder.Memory, m.Session, out _) || !finder.Memory.TryRead(m.Session + Mvs.SessionState, out byte state))
        {
            return Sampled.Gone;
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
            return Sampled.Playing;
        }
        if (state is 7 or 8 && m.Stats != null && m.StartFrame != 0)
        {
            int frames = m.LastFrame - m.StartFrame, total = m.LastResim - m.StartResim;
            finder.Memory.TryRead(m.Session + Mvs.SessionInputDelay, out int delay);
            string summary = $"NETSTATS-SUMMARY frames={frames} resim_total={total} rollbacks={m.Rollbacks} mean_depth={(m.Rollbacks > 0 ? m.DepthSum / m.Rollbacks : 0):F2} max_depth={m.MaxDepth} final_delay={delay} state={state}";
            m.Stats.Info(summary);
            log.Info(summary);
            EndMatch(m, "match ended");
            return Sampled.Idle;
        }
        if (state is 6 or 9)
        {
            return Sampled.Gone;
        }

        return Sampled.Idle;
    }

    /// <summary>A fresh log and fresh counters, named for the match if it can be read now.</summary>
    private void BeginMatch(Match m)
    {
        m.StartFrame = m.LastFrame = m.LastResim = m.StartResim = 0;
        m.Rollbacks = m.MaxDepth = m.Rollbacks1s = m.MaxDepth1s = m.ResimAtSecond = 0;
        m.DepthSum = 0;
        m.DescribeAttempts = 1;
        m.Described = false;
        MatchInfo info = DescribeMatch(m.Session, m.DescribeAttempts);
        m.Stats = openMatchLog(info);
        m.Described = info.IsComplete;
        if (!info.IsEmpty)
        {
            nameMatchLog?.Invoke(m.Stats, info);
        }

        log.Info($"[NetStats] match started on session 0x{m.Session:X} ({info}); match log opened");
    }

    /// <summary>A later reading of the description, for a match whose first reading was incomplete.</summary>
    private void Describe(Match m)
    {
        m.DescribeAttempts++;
        MatchInfo info = DescribeMatch(m.Session, m.DescribeAttempts);
        if (!info.IsEmpty && m.Stats != null)
        {
            nameMatchLog?.Invoke(m.Stats, info);
        }

        if (info.IsComplete)
        {
            m.Described = true;
            log.Info($"[NetStats] match described on reading {m.DescribeAttempts}: {info}");
        }
        else if (m.DescribeAttempts >= MaxDescribeAttempts)
        {
            log.Warn($"[NetStats] the match on session 0x{m.Session:X} was not fully described in {MaxDescribeAttempts} readings ({info}); its log is named for what was read (what each reading saw is at debug level)");
        }
    }

    /// <summary>
    /// The match id and the players' names, for the log's name. Every fighter pawn in play
    /// (AMvsFixedCharacter, the class that declares the struct; other PfgFixedPawns are shorter
    /// and are not read) carries the FGameplayPlayerData it was spawned with: username, team and
    /// player index; the UMvsGameplayConfig subsystem carries the match id. A pawn's name is
    /// taken only when the struct's player index agrees with the pawn's own. Anything unreadable
    /// leaves the field empty.
    /// <para>
    /// Every pointer followed is checked for being an object and the counts at each step go to
    /// the log at debug level, with the objects themselves when the result is incomplete. The
    /// offsets were read from the UE4SS dump and confirmed on the live game (2026-09-24), but
    /// this build scrambles at least one engine pointer (see <see cref="ObjectArray"/>), so a
    /// reading that comes back empty has to say which link broke.
    /// </para>
    /// </summary>
    private MatchInfo DescribeMatch(nint session, int attempt)
    {
        try
        {
            finder.Memory.TryRead(session + Mvs.SessionPlayerIndex, out int localIndex);
            nint fighterClass = finder.FindClass("MvsFixedCharacter");
            nint configClass = finder.FindClass("MvsGameplayConfig");
            var detail = new List<string>();

            string matchId = "";
            nint config = finder.FindInstanceOfClass(configClass);
            bool idReads = config != 0 && GameStrings.TryReadFString(finder.Memory, config + Mvs.GameplayConfigContainerMatchId, out matchId);
            if (!idReads)
            {
                matchId = "";
            }

            detail.Add($"config {(config != 0 ? Identify(config) : "none")} match_id={(idReads ? $"'{matchId}'" : config != 0 ? "unreadable " + FStringHeader(config + Mvs.GameplayConfigContainerMatchId) : "-")}");

            // This match's players: index, team and name, in player-index order.
            int pawns = 0;
            var players = new List<(int Index, int Team, string Name)>();
            foreach (nint pawn in finder.FindInstancesOfClass(fighterClass))
            {
                pawns++;
                nint data = pawn + Mvs.FixedCharacterGameplayPlayerData;
                finder.Memory.TryRead(pawn + Mvs.PawnPlayerIndex, out int pawnIndex);
                finder.Memory.TryRead(data + Mvs.GameplayPlayerDataPlayerIndex, out int index);
                finder.Memory.TryRead(data + Mvs.GameplayPlayerDataTeamIndex, out int team);
                bool nameReads = GameStrings.TryReadFString(finder.Memory, data + Mvs.GameplayPlayerDataUsername, out string name);
                if (detail.Count < DetailCap)
                {
                    detail.Add($"pawn {Identify(pawn)} index={pawnIndex} data.index={index} team={team} username={(nameReads ? $"'{name}'" : "unreadable " + FStringHeader(data + Mvs.GameplayPlayerDataUsername))}");
                }

                if (index == pawnIndex && nameReads && name.Length > 0 && !players.Any(p => p.Index == index))
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
            var info = new MatchInfo(matchId, teammates, opponents);
            log.Debug($"[NetStats] describe reading {attempt}: local index {localIndex}, class MvsFixedCharacter 0x{fighterClass:X}, class MvsGameplayConfig 0x{configClass:X}, {pawns} fighters of which {players.Count} carry a name at an index that agrees: {info}");
            if (!info.IsComplete)
            {
                foreach (string line in detail)
                {
                    log.Debug($"[NetStats]   {line}");
                }
            }

            return info;
        }
        catch (Exception e)
        {
            log.Warn($"[NetStats] could not describe the match (reading {attempt}): {e.Message}");
            return new MatchInfo("", [], []);
        }
    }

    /// <summary>
    /// An address as an object, or why it is not one: the check that tells a wrong offset or a
    /// scrambled pointer from a real link. Names are asked of the engine only for objects the
    /// object array lists, since a name index read off memory that is not an object would go to
    /// FName::ToString unchecked; without the array (heap scan) the name is left out.
    /// </summary>
    private string Identify(nint address)
    {
        if (!ObjectHeader.TryRead(finder.Memory, address, out var h))
        {
            return $"0x{address:X} (unreadable)";
        }

        if (!finder.Image.Contains(h.VTable))
        {
            return $"0x{address:X} (not an object: vtable 0x{h.VTable:X} outside the image)";
        }

        if (!finder.UsesObjectArray)
        {
            return $"0x{address:X} (vtable in the image; no object array to name it by)";
        }

        if (!finder.IsLive(address))
        {
            return $"0x{address:X} (vtable in the image but not in the object array: freed, or not an object)";
        }

        string name = finder.Names.ToString(h.Name) ?? "<unresolved name>";
        string cls = finder.IsLive(h.ClassPrivate) && ObjectHeader.TryRead(finder.Memory, h.ClassPrivate, out var c) && finder.Image.Contains(c.VTable)
            ? finder.Names.ToString(c.Name) ?? "<unresolved class name>"
            : $"<no live class object at 0x{h.ClassPrivate:X}>";
        return $"0x{address:X} ({cls} '{name}')";
    }

    /// <summary>The TArray header of an FString that did not read, so the log shows whether it is a wrong offset or an empty field.</summary>
    private string FStringHeader(nint address) =>
        finder.Memory.TryRead(address, out TArrayHeader h) ? $"(data=0x{h.Data:X} count={h.Count} max={h.Max})" : "(header unreadable)";

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
