using OpenVersus.Game;

namespace OpenVersus.Tests;

public class StockRulesTests
{
    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(0, StockRules.DefaultLives)]
    [InlineData(-1, StockRules.DefaultLives)]
    [InlineData(100, StockRules.DefaultLives)]
    public void LivesAreTheServersRingoutsWhenUsable(int ringouts, int lives)
    {
        Assert.Equal(lives, StockRules.LivesFromRingouts(ringouts));
    }

    [Fact]
    public void OnlyTheFfaModeTurnsTheRulesOn()
    {
        Assert.True(StockRules.IsFfa("FFA"));
        Assert.True(StockRules.IsFfa("ffa"));
        Assert.False(StockRules.IsFfa("2v2"));
        Assert.False(StockRules.IsFfa(null));
    }

    [Fact]
    public void TheLastFighterStandingWinsForTheirTeam()
    {
        var match = new StockRules(3);
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
        var match = new StockRules(1);
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
        var match = new StockRules(3);
        match.Register(1, playerIndex: 0, team: 0);
        Assert.True(match.SkipsRespawn(1, 0));
        Assert.False(match.SkipsRespawn(1, 1));
        Assert.False(match.SkipsRespawn(1, -1));
        Assert.False(match.SkipsRespawn(99, 0));
    }

    [Fact]
    public void LivesLeftFollowTheDeaths()
    {
        var match = new StockRules(3);
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
        var match = new StockRules(2);
        match.Register(1, playerIndex: 0, team: 0);
        match.Register(2, playerIndex: 1, team: 1);
        match.Died(1, respawnsRemaining: 0);
        Assert.Equal(1, match.EliminatedCount);

        match.Register(1, playerIndex: 0, team: 0);
        Assert.Equal(0, match.EliminatedCount);
        Assert.Equal(2, match.FighterCount);

        match.Register(3, playerIndex: -1, team: 0);
        match.Register(4, playerIndex: StockRules.MaxFighters, team: 0);
        match.Register(0, playerIndex: 2, team: 0);
        Assert.Equal(2, match.FighterCount);
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(1, 1)]
    [InlineData(0, StockRules.DefaultIndividualLives)]
    [InlineData(100, StockRules.DefaultIndividualLives)]
    public void IndividualStocksLivesAreHalfTheRingoutsRoundedUp(int ringouts, int lives)
    {
        Assert.Equal(lives, StockRules.IndividualLivesFromRingouts(ringouts));
    }

    [Fact]
    public void TheRulesComeFromTheModeAndTheSelectedMutator()
    {
        var ffa = StockRules.For(new MatchSettings("FFA", 4, [], 5, true));
        Assert.NotNull(ffa);
        Assert.True(ffa.LastFighterWins);
        Assert.Equal(4, ffa.Lives);

        var individual = StockRules.For(new MatchSettings("2v2", 4, ["ovs_friendly_fire", "OVS_2v2_Individual_Stocks"], 5, true));
        Assert.NotNull(individual);
        Assert.False(individual.LastFighterWins);
        Assert.Equal(2, individual.Lives);

        Assert.Null(StockRules.For(new MatchSettings("2v2", 4, ["ovs_friendly_fire"], 5, true)));
        Assert.Null(StockRules.For(new MatchSettings("1v1", 3, [], 1, true)));
        // The Lab can read "ffa" too, and must never keep a fighter down.
        Assert.Null(StockRules.For(new MatchSettings("ffa", -1, [], MatchSettings.LabMatchType, false)));
    }

    [Fact]
    public void IndividualStocksKeepFightersDownButLeaveTheEndToTheGame()
    {
        var match = new StockRules(2, lastFighterWins: false);
        match.Register(1, playerIndex: 0, team: 0);
        match.Register(2, playerIndex: 1, team: 0);
        match.Register(3, playerIndex: 2, team: 1);
        match.Register(4, playerIndex: 3, team: 1);

        Assert.Null(match.Died(1, respawnsRemaining: 1));
        Assert.Null(match.Died(1, respawnsRemaining: 0));
        Assert.True(match.SkipsRespawn(1, 0));
        Assert.Null(match.Died(2, respawnsRemaining: 0));
        Assert.Null(match.Died(3, respawnsRemaining: 0));
        // Three of four out: FFA would end it; the game's team score does here.
        Assert.Equal(3, match.EliminatedCount);
        Assert.False(match.Ended);
    }
}
