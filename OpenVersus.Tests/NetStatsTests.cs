using OpenVersus.Game;
using OpenVersus.Memory;
using OpenVersus.NetStats;

namespace OpenVersus.Tests;

/// <summary>The netstats sampler against a fake session: one match log per match, the summary in both, the
/// session found again after each match, and nothing asked of the engine before it is up. Shares the
/// "engine readiness" collection with EngineTests.</summary>
[Collection("engine readiness")]
public class NetStatsTests
{
    /// <summary>A match log that records whether it was closed and what it was named for.</summary>
    private sealed class MatchLog : IDisposable
    {
        public ListLogger Lines { get; } = new();
        public bool Closed { get; private set; }
        public MatchInfo? NamedFor { get; set; }
        public void Dispose() => Closed = true;
    }

    private sealed class MatchLogFactory
    {
        public List<MatchLog> Opened { get; } = [];
        public List<MatchInfo> Infos { get; } = [];

        public Microsoft.Extensions.Logging.ILogger Open(MatchInfo info)
        {
            var log = new MatchLog();
            Opened.Add(log);
            Infos.Add(info);
            return new ClosableLogger(log);
        }

        public void Name(Microsoft.Extensions.Logging.ILogger log, MatchInfo info) => ((ClosableLogger)log).Match.NamedFor = info;
    }

    /// <summary>What the factory hands out: an ILogger that is also disposable, as a Log is.</summary>
    private sealed class ClosableLogger(MatchLog log) : Microsoft.Extensions.Logging.ILogger, IDisposable
    {
        public MatchLog Match => log;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            ((Microsoft.Extensions.Logging.ILogger)log.Lines).Log(logLevel, eventId, state, exception, formatter);
        public void Dispose() => log.Dispose();
    }

    /// <summary>A netcode session in the fake game with a state manager, in the given state.</summary>
    private static (nint Session, nint StateManager) AddSession(FakeGame game, nint sessionClass, byte state, int frame, int ping)
    {
        nint session = game.AddLargeInstance(sessionClass, "Session", 0x800);
        nint stateManager = game.AddBlock(0x500);
        game.Memory.Write(session + Mvs.SessionStateManager, (long)stateManager);
        game.Memory.Write(stateManager + Mvs.StateManagerCurrentFrame, frame);
        game.Memory.Write(session + Mvs.SessionState, state);
        game.Memory.Write(session + Mvs.SessionPlayerIndex, 1);
        game.Memory.Write(session + Mvs.SessionInputDelay, 3);
        game.Memory.Write(session + Mvs.SessionPing, ping);
        game.Memory.Write(session + Mvs.SessionResimCount, 10);
        return (session, stateManager);
    }

    /// <summary>
    /// A 2v2 as the description reads it: four fighter pawns in play, each carrying the player data
    /// it was spawned with (username, team, index), and the gameplay config with the match id. The
    /// local player is index 1 on team 1, with index 3 as the teammate. Listed before them is a
    /// pawn that is not a fighter (a hazard, say): it is shorter than the struct's offset, but the
    /// bytes there happen to agree with its index, so only the class can exclude it. With
    /// <paramref name="filled"/> false the names and the id are left empty, as they might be early
    /// in a match; the returned action fills them in.
    /// </summary>
    private static Action AddPlayers(FakeGame game, bool filled)
    {
        nint pawnClass = game.AddClass("PfgFixedPawn");
        nint fighterClass = game.AddClass("MvsFixedCharacter", super: pawnClass);
        nint configClass = game.AddClass("MvsGameplayConfig");
        var strings = new List<(nint Field, string Text)>();
        nint stray = game.AddLargeInstance(pawnClass, "Hazard", 0x700);
        game.Memory.Write(stray + Mvs.PawnPlayerIndex, 0);
        game.Memory.Write(stray + Mvs.FixedCharacterGameplayPlayerData + Mvs.GameplayPlayerDataPlayerIndex, 0);
        game.Memory.Write(stray + Mvs.FixedCharacterGameplayPlayerData + Mvs.GameplayPlayerDataTeamIndex, 0);
        strings.Add((stray + Mvs.FixedCharacterGameplayPlayerData + Mvs.GameplayPlayerDataUsername, "Not A Player"));
        foreach (var (index, team, name) in new[] { (1, 1, "Me"), (0, 0, "Rival Name!"), (3, 1, "Buddy"), (2, 0, "Other") })
        {
            nint pawn = game.AddLargeInstance(fighterClass, "Fighter", 0x700);
            nint data = pawn + Mvs.FixedCharacterGameplayPlayerData;
            game.Memory.Write(pawn + Mvs.PawnPlayerIndex, index);
            game.Memory.Write(data + Mvs.GameplayPlayerDataPlayerIndex, index);
            game.Memory.Write(data + Mvs.GameplayPlayerDataTeamIndex, team);
            strings.Add((data + Mvs.GameplayPlayerDataUsername, name));
        }

        nint config = game.AddLargeInstance(configClass, "GameplayConfig", 0x200);
        strings.Add((config + Mvs.GameplayConfigContainerMatchId, "match-777"));

        void Fill()
        {
            foreach (var (field, text) in strings)
            {
                // The FString headers live inline in the object: copy the 16-byte header into place.
                nint header = game.AddFString(text);
                game.Memory.TryRead(header, out TArrayHeader h);
                game.Memory.Write(field, (long)h.Data);
                game.Memory.Write(field + 8, h.Count);
                game.Memory.Write(field + 12, h.Max);
            }
        }

        if (filled)
        {
            Fill();
        }

        return Fill;
    }

    private static NetStatsLogger Start(FakeGame game, ListLogger main, MatchLogFactory factory, TimeSpan findInterval)
    {
        Engine.ReadyOverride = () => true;
        var logger = new NetStatsLogger(game.Finder(), main, factory.Open, factory.Name, findInterval);
        logger.Start();
        return logger;
    }

    [Fact]
    public void EachMatchGetsItsOwnLogAndTheSummaryGoesToBoth()
    {
        var game = new FakeGame();
        nint sessionClass = game.AddClass("PfgNetcodeSession");
        var (session, stateManager) = AddSession(game, sessionClass, state: 4, frame: 100, ping: 42);
        AddPlayers(game, filled: true);

        var main = new ListLogger();
        var factory = new MatchLogFactory();
        bool ready = false;
        Engine.ReadyOverride = () => ready;
        var logger = new NetStatsLogger(game.Finder(), main, factory.Open, factory.Name, findInterval: TimeSpan.FromMilliseconds(20));
        logger.Start();
        try
        {
            // Nothing is asked of the engine until the game says it is up.
            Assert.True(WaitFor(() => main.Lines.Any(l => l.Contains("waiting for the game")), TimeSpan.FromSeconds(5)));
            Thread.Sleep(200);
            Assert.Empty(factory.Opened);
            ready = true;

            // Match one: a log opens at the first playing sample, is named for the match, and takes the per-second lines.
            Assert.True(WaitFor(() => factory.Opened.Count == 1 && factory.Opened[0].Lines.Lines.Any(l => l.StartsWith("NETSTATS player=1 ")), TimeSpan.FromSeconds(10)), "no NETSTATS line within 10 s");
            var first = factory.Opened[0];
            Assert.Equal("match-777", factory.Infos[0].MatchId);
            Assert.Equal(["Buddy"], factory.Infos[0].Teammates);
            Assert.Equal(["Rival Name!", "Other"], factory.Infos[0].Opponents);
            Assert.Equal("_match-777_with_Buddy_vs_Rival_Name+Other", factory.Infos[0].FileSuffix);
            Assert.Equal(factory.Infos[0], first.NamedFor);
            Assert.Contains(main.Lines, l => l.Contains("match match-777 with Buddy vs Rival Name!, Other"));
            Assert.Contains(main.Lines, l => l.Contains($"[NetStats] session 0x{session:X}"));
            Assert.Contains(main.Lines, l => l.Contains("match log opened"));
            Assert.DoesNotContain(main.Lines, l => l.StartsWith("NETSTATS player="));
            string line = first.Lines.Lines.First(l => l.StartsWith("NETSTATS player=1 "));
            Assert.Contains("delay=3 ping=42", line);
            Assert.Contains("frame=100", line);

            // A rollback of depth three, then the match ends: summary in both, match log closed.
            game.Memory.Write(session + Mvs.SessionResimCount, 13);
            game.Memory.Write(stateManager + Mvs.StateManagerCurrentFrame, 700);
            Assert.True(WaitFor(() => first.Lines.Lines.Any(l => l.Contains("resim_total=3")), TimeSpan.FromSeconds(10)), "rollback not sampled");
            game.Memory.Write(session + Mvs.SessionState, (byte)7);
            Assert.True(WaitFor(() => first.Closed, TimeSpan.FromSeconds(10)), "match log not closed at state 7");
            string summary = main.Lines.First(l => l.StartsWith("NETSTATS-SUMMARY"));
            Assert.Contains("frames=600 resim_total=3 rollbacks=1 mean_depth=3.00 max_depth=3 final_delay=3 state=7", summary);
            Assert.Contains(first.Lines.Lines, l => l == summary);
            Assert.Single(factory.Opened);

            // Lingering in the ended state opens nothing. Playing again on the same session, which is
            // still in the object array, is a new match with fresh counters.
            Thread.Sleep(200);
            Assert.Single(factory.Opened);
            game.Memory.Write(session + Mvs.SessionResimCount, 13);
            game.Memory.Write(stateManager + Mvs.StateManagerCurrentFrame, 1000);
            game.Memory.Write(session + Mvs.SessionState, (byte)4);
            Assert.True(WaitFor(() => factory.Opened.Count == 2 && factory.Opened[1].Lines.Lines.Any(l => l.StartsWith("NETSTATS ")), TimeSpan.FromSeconds(10)), "second match log not opened");
            var second = factory.Opened[1];
            Assert.Contains("resim_total=0", second.Lines.Lines.First(l => l.StartsWith("NETSTATS ")));
            Assert.False(second.Closed);
            game.Memory.Write(stateManager + Mvs.StateManagerCurrentFrame, 1300);
            // The summary reports the last sampled frame, so let a sample see 1300 before ending the match.
            Assert.True(WaitFor(() => second.Lines.Lines.Any(l => l.Contains("frame=1300")), TimeSpan.FromSeconds(10)), "frame 1300 not sampled");
            game.Memory.Write(session + Mvs.SessionState, (byte)8);
            Assert.True(WaitFor(() => second.Closed, TimeSpan.FromSeconds(10)), "second match log not closed");
            Assert.Equal(2, main.Lines.Count(l => l.StartsWith("NETSTATS-SUMMARY")));
            Assert.Contains("frames=300 resim_total=0 rollbacks=0", main.Lines.Last(l => l.StartsWith("NETSTATS-SUMMARY")));
        }
        finally
        {
            logger.Stop();
            Engine.ReadyOverride = null;
        }
    }

    /// <summary>
    /// The failure of 2026-09-24: the game freed the session after the first match, the logger kept
    /// sampling the address, and a state byte of 4 in the reused memory opened a second "match" of
    /// garbage. A freed session leaves the object array, which every sample asks; the find interval
    /// here is far longer than the test, so only that check can notice.
    /// </summary>
    [Fact]
    public void AFreedSessionIsDroppedAndTheNextMatchsSessionIsFound()
    {
        var game = new FakeGame();
        nint sessionClass = game.AddClass("PfgNetcodeSession");
        var (session, stateManager) = AddSession(game, sessionClass, state: 4, frame: 100, ping: 11);

        var main = new ListLogger();
        var factory = new MatchLogFactory();
        var logger = Start(game, main, factory, TimeSpan.FromSeconds(30));
        try
        {
            Assert.True(WaitFor(() => factory.Opened.Count == 1 && factory.Opened[0].Lines.Lines.Any(l => l.StartsWith("NETSTATS ")), TimeSpan.FromSeconds(10)), "first match log not opened");
            game.Memory.Write(stateManager + Mvs.StateManagerCurrentFrame, 400);
            Assert.True(WaitFor(() => factory.Opened[0].Lines.Lines.Any(l => l.Contains("frame=400")), TimeSpan.FromSeconds(10)), "frame 400 not sampled");
            game.Memory.Write(session + Mvs.SessionState, (byte)7);
            Assert.True(WaitFor(() => factory.Opened[0].Closed, TimeSpan.FromSeconds(10)), "match log not closed at state 7");

            // The next match's session is created, then the game frees the old one and its memory
            // is reused: the bytes still read, and the byte at the state offset happens to be 4 again.
            var (next, _) = AddSession(game, sessionClass, state: 4, frame: 5, ping: 77);
            game.RemoveObject(session);
            game.Memory.Write(session + Mvs.SessionStateManager, 0x1043871232L);
            game.Memory.Write(session + Mvs.SessionPing, -1811147703);
            game.Memory.Write(session + Mvs.SessionState, (byte)4);
            Assert.True(WaitFor(() => main.Lines.Any(l => l.Contains("[NetStats] session gone")), TimeSpan.FromSeconds(10)), "the freed session was not noticed");
            Assert.True(WaitFor(() => factory.Opened.Count == 2 && factory.Opened[1].Lines.Lines.Any(l => l.StartsWith("NETSTATS ")), TimeSpan.FromSeconds(10)), "second match log not opened");
            Assert.Contains(main.Lines, l => l.Contains($"[NetStats] session 0x{next:X}"));
            Assert.Contains("ping=77", factory.Opened[1].Lines.Lines.First(l => l.StartsWith("NETSTATS ")));
            Assert.Contains("frame=5", factory.Opened[1].Lines.Lines.First(l => l.StartsWith("NETSTATS ")));
        }
        finally
        {
            logger.Stop();
            Engine.ReadyOverride = null;
        }
    }

    /// <summary>
    /// An ended session can stay in the object array until the garbage collector runs while the
    /// next match's session is created after it, so the lookup takes the playing one over the one
    /// held, and says so.
    /// </summary>
    [Fact]
    public void APlayingSessionWinsOverAnEndedOneThatIsStillListed()
    {
        var game = new FakeGame();
        nint sessionClass = game.AddClass("PfgNetcodeSession");
        var (old, oldManager) = AddSession(game, sessionClass, state: 4, frame: 100, ping: 11);

        var main = new ListLogger();
        var factory = new MatchLogFactory();
        var logger = Start(game, main, factory, TimeSpan.FromMilliseconds(20));
        try
        {
            Assert.True(WaitFor(() => factory.Opened.Count == 1 && factory.Opened[0].Lines.Lines.Any(l => l.StartsWith("NETSTATS ")), TimeSpan.FromSeconds(10)), "first match log not opened");
            game.Memory.Write(oldManager + Mvs.StateManagerCurrentFrame, 400);
            Assert.True(WaitFor(() => factory.Opened[0].Lines.Lines.Any(l => l.Contains("frame=400")), TimeSpan.FromSeconds(10)), "frame 400 not sampled");
            game.Memory.Write(old + Mvs.SessionState, (byte)7);
            Assert.True(WaitFor(() => factory.Opened[0].Closed, TimeSpan.FromSeconds(10)), "match log not closed at state 7");

            // The old session lingers, listed and ended; the new one is listed after it and starts playing.
            var (next, _) = AddSession(game, sessionClass, state: 4, frame: 5, ping: 77);
            Assert.True(WaitFor(() => factory.Opened.Count == 2 && factory.Opened[1].Lines.Lines.Any(l => l.StartsWith("NETSTATS ")), TimeSpan.FromSeconds(10)), "second match log not opened");
            Assert.Contains(main.Lines, l => l == $"[NetStats] session 0x{next:X} is playing while 0x{old:X} is still listed; switching");
            Assert.Contains(main.Lines, l => l.Contains($"[NetStats] session 0x{next:X} (2 listed)"));
            Assert.Contains("ping=77", factory.Opened[1].Lines.Lines.First(l => l.StartsWith("NETSTATS ")));
        }
        finally
        {
            logger.Stop();
            Engine.ReadyOverride = null;
        }
    }

    /// <summary>
    /// The description may not be readable at the first playing sample. It is read again once a
    /// second, the log is named when a reading succeeds, and each empty reading says what it saw.
    /// </summary>
    [Fact]
    public void TheDescriptionIsReadAgainUntilThePlayersCanBeRead()
    {
        var game = new FakeGame();
        nint sessionClass = game.AddClass("PfgNetcodeSession");
        AddSession(game, sessionClass, state: 4, frame: 100, ping: 42);
        Action fill = AddPlayers(game, filled: false);

        var main = new ListLogger();
        var factory = new MatchLogFactory();
        var logger = Start(game, main, factory, TimeSpan.FromMilliseconds(20));
        try
        {
            Assert.True(WaitFor(() => factory.Opened.Count == 1 && factory.Opened[0].Lines.Lines.Any(l => l.StartsWith("NETSTATS ")), TimeSpan.FromSeconds(10)), "match log not opened");
            var first = factory.Opened[0];
            Assert.True(factory.Infos[0].IsEmpty);
            Assert.Null(first.NamedFor);
            Assert.Contains(main.Lines, l => l.Contains("(match ? vs ?)"));

            // The empty reading was explained: counts at each step, and the objects it looked at.
            Assert.Contains(main.Lines, l => l.Contains("describe reading 1:") && l.Contains("4 fighters of which 0 carry a name at an index that agrees"));
            Assert.Contains(main.Lines, l => l.Contains("config 0x") && l.Contains("(MvsGameplayConfig 'GameplayConfig') match_id=''"));
            Assert.Contains(main.Lines, l => l.Contains("pawn 0x") && l.Contains("(MvsFixedCharacter 'Fighter') index=1 data.index=1 team=1 username=''"));

            // Once the names and the id are filled in, the next reading names the log.
            fill();
            Assert.True(WaitFor(() => first.NamedFor != null, TimeSpan.FromSeconds(10)), "the log was not named after the players became readable");
            Assert.Equal("match-777", first.NamedFor!.MatchId);
            Assert.Equal(["Rival Name!", "Other"], first.NamedFor.Opponents);
            Assert.Contains(main.Lines, l => l.Contains("match described on reading") && l.Contains("match match-777 with Buddy vs Rival Name!, Other"));
            Assert.False(first.Closed);
        }
        finally
        {
            logger.Stop();
            Engine.ReadyOverride = null;
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }
}
