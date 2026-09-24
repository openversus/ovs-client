using OpenVersus.Game;
using OpenVersus.NetStats;

namespace OpenVersus.Tests;

/// <summary>The netstats sampler against a fake session: lines go to the stats log, the summary to both.</summary>
[Collection("engine readiness")]
public class NetStatsTests
{
    [Fact]
    public void SamplesAPlayingSessionIntoItsOwnLogAndSummarizesToBoth()
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

        var main = new ListLogger();
        var stats = new ListLogger();
        bool ready = false;
        Engine.ReadyOverride = () => ready;
        var logger = new NetStatsLogger(game.Finder(), main, stats, findInterval: TimeSpan.FromMilliseconds(20));
        logger.Start();
        try
        {
            // Nothing is asked of the engine until the game says it is up.
            Assert.True(WaitFor(() => main.Lines.Any(l => l.Contains("waiting for the game")), TimeSpan.FromSeconds(5)));
            Thread.Sleep(200);
            Assert.DoesNotContain(main.Lines, l => l.Contains("[NetStats] session"));
            Assert.Empty(stats.Lines);
            ready = true;

            Assert.True(WaitFor(() => stats.Lines.Any(l => l.StartsWith("NETSTATS player=1 ")), TimeSpan.FromSeconds(10)), "no NETSTATS line within 10 s");
            Assert.Contains(main.Lines, l => l.Contains($"[NetStats] session 0x{session:X}"));
            Assert.DoesNotContain(main.Lines, l => l.StartsWith("NETSTATS player="));
            string line = stats.Lines.First(l => l.StartsWith("NETSTATS player=1 "));
            Assert.Contains("delay=3 ping=42", line);
            Assert.Contains("frame=100", line);

            // A rollback: the resim counter jumps by 3, then the match ends locally.
            game.Memory.Write(session + Mvs.SessionResimCount, 13);
            game.Memory.Write(stateManager + Mvs.StateManagerCurrentFrame, 700);
            Assert.True(WaitFor(() => stats.Lines.Any(l => l.Contains("resim_total=3")), TimeSpan.FromSeconds(10)), "rollback not sampled");
            game.Memory.Write(session + Mvs.SessionState, (byte)7);
            Assert.True(WaitFor(() => main.Lines.Any(l => l.StartsWith("NETSTATS-SUMMARY")), TimeSpan.FromSeconds(10)), "no summary");
            string summary = main.Lines.First(l => l.StartsWith("NETSTATS-SUMMARY"));
            Assert.Contains("frames=600 resim_total=3 rollbacks=1 mean_depth=3.00 max_depth=3 final_delay=3 state=7", summary);
            Assert.Contains(stats.Lines, l => l == summary);
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
