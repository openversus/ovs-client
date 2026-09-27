using OpenVersus.Game;

namespace OpenVersus.Tests;

public class FfaStocksTests
{
    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(0, FfaStocks.DefaultLives)]
    [InlineData(-1, FfaStocks.DefaultLives)]
    [InlineData(100, FfaStocks.DefaultLives)]
    public void LivesAreTheServersRingoutsWhenUsable(int ringouts, int lives)
    {
        Assert.Equal(lives, FfaStocks.LivesFromRingouts(ringouts));
    }

    [Fact]
    public void OnlyTheFfaModeTurnsTheRulesOn()
    {
        Assert.True(FfaStocks.IsFfa("FFA"));
        Assert.True(FfaStocks.IsFfa("ffa"));
        Assert.False(FfaStocks.IsFfa("2v2"));
        Assert.False(FfaStocks.IsFfa(null));
    }

    [Fact]
    public void TheLastFighterStandingWinsForTheirTeam()
    {
        var match = new FfaStocks(3);
        match.Register(1, playerIndex: 0, team: 0);
        match.Register(2, playerIndex: 1, team: 1);
        match.Register(3, playerIndex: 2, team: 2);
        Assert.Equal(2, match.InitialRespawns);

        // Deaths with lives left never end the match; RespawnsRemaining 0 at a death is the last life.
        Assert.Null(match.Died(1, respawnsRemaining: 2));
        Assert.Null(match.Died(1, respawnsRemaining: 1));
        Assert.Null(match.Died(1, respawnsRemaining: 0));
        Assert.Equal(1, match.EliminatedCount);
        Assert.False(match.Ended);

        // The second fighter out leaves player 1 (team 1), who never died.
        Assert.Equal(1, match.Died(3, respawnsRemaining: 0));
        Assert.True(match.Ended);
    }

    [Fact]
    public void TheWinningTeamIsTheSurvivors()
    {
        var match = new FfaStocks(1);
        match.Register(10, playerIndex: 0, team: 4);
        match.Register(20, playerIndex: 1, team: 7);
        Assert.Equal(0, match.InitialRespawns);

        Assert.Equal(7, match.Died(10, respawnsRemaining: 0));
        Assert.True(match.Ended);
        // Once ended, later deaths decide nothing.
        Assert.Null(match.Died(20, respawnsRemaining: 0));
    }

    [Fact]
    public void OnlyARegisteredFighterAtZeroSkipsItsRespawn()
    {
        var match = new FfaStocks(3);
        match.Register(1, playerIndex: 0, team: 0);
        Assert.True(match.SkipsRespawn(1, 0));
        Assert.False(match.SkipsRespawn(1, 1));
        Assert.False(match.SkipsRespawn(1, -1));
        Assert.False(match.SkipsRespawn(99, 0));
    }

    [Fact]
    public void LivesLeftFollowTheDeaths()
    {
        var match = new FfaStocks(3);
        match.Register(1, playerIndex: 0, team: 0);
        match.Register(2, playerIndex: 1, team: 1);
        match.Died(2, respawnsRemaining: 2);
        int[] lives = match.LivesLeft();
        Assert.Equal(3, lives[0]);
        Assert.Equal(2, lives[1]);
    }

    [Fact]
    public void RegisteringAgainStartsTheFighterOverAndBadIndexesAreIgnored()
    {
        var match = new FfaStocks(2);
        match.Register(1, playerIndex: 0, team: 0);
        match.Register(2, playerIndex: 1, team: 1);
        match.Died(1, respawnsRemaining: 0);
        Assert.Equal(1, match.EliminatedCount);

        match.Register(1, playerIndex: 0, team: 0);
        Assert.Equal(0, match.EliminatedCount);
        Assert.Equal(2, match.FighterCount);

        match.Register(3, playerIndex: -1, team: 0);
        match.Register(4, playerIndex: FfaStocks.MaxFighters, team: 0);
        match.Register(0, playerIndex: 2, team: 0);
        Assert.Equal(2, match.FighterCount);
    }
}
