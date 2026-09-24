using OpenVersus.Game;
using OpenVersus.Memory;
using OpenVersus.NetStats;

namespace OpenVersus.Tests;

/// <summary>The netstats sampler against a fake session: one match log per match, the summary in both, and
/// nothing asked of the engine before it is up. Shares the "engine readiness" collection with EngineTests.</summary>
[Collection("engine readiness")]
public class NetStatsTests
{
    /// <summary>A match log that records whether it was closed.</summary>
    private sealed class MatchLog : IDisposable
    {
        public ListLogger Lines { get; } = new();
        public bool Closed { get; private set; }
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
    }

    /// <summary>What the factory hands out: an ILogger that is also disposable, as a Log is.</summary>
    private sealed class ClosableLogger(MatchLog log) : Microsoft.Extensions.Logging.ILogger, IDisposable
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            ((Microsoft.Extensions.Logging.ILogger)log.Lines).Log(logLevel, eventId, state, exception, formatter);
        public void Dispose() => log.Dispose();
    }

    [Fact]
    public void EachMatchGetsItsOwnLogAndTheSummaryGoesToBoth()
    {
        var game = new FakeGame();
        nint sessionClass = game.AddClass("PfgNetcodeSession");
        nint session = game.AddLargeInstance(sessionClass, "Session", 0x800);
        nint stateManager = game.AddBlock(0x500);
        game.Memory.Write(session + Mvs.SessionStateManager, (long)stateManager);
        game.Memory.Write(stateManager + Mvs.StateManagerCurrentFrame, 100);
        game.Memory.Write(session + Mvs.SessionState, (byte)4);
        game.Memory.Write(session + Mvs.SessionPlayerIndex, 1);
        game.Memory.Write(session + Mvs.SessionInputDelay, 3);
        game.Memory.Write(session + Mvs.SessionPing, 42);
        game.Memory.Write(session + Mvs.SessionResimCount, 10);

        // Two pawns in play with their player states, and match data for both plus a stale
        // object from an earlier match whose player state nothing in play points at.
        nint pawnClass = game.AddClass("PfgFixedPawn");
        nint dataClass = game.AddBlueprintClass("MatchPlayerData_C");
        nint stateClass = game.AddClass("PlayerState");
        nint localState = game.AddInstance(stateClass, "LocalState");
        nint rivalState = game.AddInstance(stateClass, "RivalState");
        nint mateState = game.AddInstance(stateClass, "MateState");
        nint rival2State = game.AddInstance(stateClass, "Rival2State");
        nint oldState = game.AddInstance(stateClass, "OldState");
        foreach (var (index, state) in new[] { (1, localState), (0, rivalState), (3, mateState), (2, rival2State) })
        {
            nint pawn = game.AddLargeInstance(pawnClass, "Pawn", 0x400);
            game.Memory.Write(pawn + Mvs.PawnPlayerIndex, index);
            game.Memory.Write(pawn + Mvs.PawnPlayerState, (long)state);
        }

        // A 2v2: teams 0 and 1; the local player is index 1 on team 1 with index 3; a ghost from an earlier match.
        foreach (var (index, team, state, name, id) in new[] { (1, 1, localState, "Me", "match-777"), (0, 0, rivalState, "Rival Name!", "match-777"), (3, 1, mateState, "Buddy", "match-777"), (2, 0, rival2State, "Other", "match-777"), (0, 0, oldState, "Ghost", "match-111") })
        {
            nint data = game.AddLargeInstance(dataClass, "MatchPlayerData", 0x100);
            nint username = game.AddFString(name);
            nint matchId = game.AddFString(id);
            // The FString headers live inline in the object: copy the 16-byte headers into place.
            game.Memory.TryRead(username, out TArrayHeader nameHeader);
            game.Memory.TryRead(matchId, out TArrayHeader idHeader);
            game.Memory.Write(data + Mvs.MatchPlayerDataUsername, (long)nameHeader.Data);
            game.Memory.Write(data + Mvs.MatchPlayerDataUsername + 8, nameHeader.Count);
            game.Memory.Write(data + Mvs.MatchPlayerDataUsername + 12, nameHeader.Max);
            game.Memory.Write(data + Mvs.MatchPlayerDataMatchId, (long)idHeader.Data);
            game.Memory.Write(data + Mvs.MatchPlayerDataMatchId + 8, idHeader.Count);
            game.Memory.Write(data + Mvs.MatchPlayerDataMatchId + 12, idHeader.Max);
            game.Memory.Write(data + Mvs.MatchPlayerDataPlayerIndex, index);
            game.Memory.Write(data + Mvs.MatchPlayerDataTeamIndex, team);
            game.Memory.Write(data + Mvs.MatchPlayerDataPlayerState, (long)state);
        }

        var main = new ListLogger();
        var factory = new MatchLogFactory();
        bool ready = false;
        Engine.ReadyOverride = () => ready;
        var logger = new NetStatsLogger(game.Finder(), main, factory.Open, findInterval: TimeSpan.FromMilliseconds(20));
        logger.Start();
        try
        {
            // Nothing is asked of the engine until the game says it is up.
            Assert.True(WaitFor(() => main.Lines.Any(l => l.Contains("waiting for the game")), TimeSpan.FromSeconds(5)));
            Thread.Sleep(200);
            Assert.Empty(factory.Opened);
            ready = true;

            // Match one: a log opens at the first playing sample and takes the per-second lines.
            Assert.True(WaitFor(() => factory.Opened.Count == 1 && factory.Opened[0].Lines.Lines.Any(l => l.StartsWith("NETSTATS player=1 ")), TimeSpan.FromSeconds(10)), "no NETSTATS line within 10 s");
            var first = factory.Opened[0];
            Assert.Equal("match-777", factory.Infos[0].MatchId);
            Assert.Equal(["Buddy"], factory.Infos[0].Teammates);
            Assert.Equal(["Rival Name!", "Other"], factory.Infos[0].Opponents);
            Assert.Equal("_match-777_with_Buddy_vs_Rival_Name+Other", factory.Infos[0].FileSuffix);
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

            // Lingering in the ended state opens nothing. Playing again is a new match with fresh counters.
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
