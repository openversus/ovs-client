namespace OpenVersus.Game;

/// <summary>
/// Stock rules for one match, without the game: every fighter has <see cref="Lives"/> lives and a
/// fighter out of lives stays down. Two rule sets use it:
/// <list type="bullet">
/// <item>Free For All: lives are the server's ringout count, and the match ends when one fighter
/// is left (<see cref="LastFighterWins"/>).</item>
/// <item>2v2 Individual Stocks, the OVS mutator: each fighter has half the team's ringouts to win
/// (rounded up), and the game's own team score ends the match, which it reaches by the time a
/// team's fighters are all out.</item>
/// </list>
/// <para>
/// The lives themselves are the game's own: APfgFixedPawn.RespawnsRemaining, which every peer
/// simulates alike (and rolls back alike). Online matches start it at -1 (unlimited); on a
/// fighter's first death it becomes <see cref="InitialRespawns"/>, the native DoRespawn counts it
/// down, and a death at 0 is the last. What this class keeps is only who is out, for ending the
/// match, and the lives each player has left, for the HUD.
/// </para>
/// </summary>
public sealed class StockRules
{
    /// <summary>The mode string the server sends for Free For All.</summary>
    public const string FfaMode = "FFA";
    /// <summary>The slug of the OVS mutator that gives each 2v2 fighter their own lives.</summary>
    public const string IndividualStocksSlug = "ovs_2v2_individual_stocks";
    /// <summary>FFA lives when the server sends no usable ringout count.</summary>
    public const int DefaultLives = 3;
    /// <summary>Individual Stocks lives when the server sends no usable ringout count: the C++ client's 2, for the default 4 to win.</summary>
    public const int DefaultIndividualLives = 2;
    /// <summary>The most lives a match takes; a larger count is treated as unusable.</summary>
    public const int MaxLives = 99;
    /// <summary>The most fighters a match has.</summary>
    public const int MaxFighters = 8;

    private readonly List<Fighter> _fighters = [];
    private readonly int[] _livesLeft = new int[MaxFighters];

    /// <summary>A match with <paramref name="lives"/> lives per fighter; <paramref name="lastFighterWins"/> for Free For All.</summary>
    public StockRules(int lives, bool lastFighterWins = true)
    {
        Lives = lives;
        LastFighterWins = lastFighterWins;
        Array.Fill(_livesLeft, lives);
    }

    /// <summary>Lives per fighter.</summary>
    public int Lives { get; }

    /// <summary>
    /// Free For All: the match ends when one fighter is left, and the game's score-based end waits
    /// for that. Otherwise (Individual Stocks) the game's own end stands.
    /// </summary>
    public bool LastFighterWins { get; }

    /// <summary>The rule set's name for the log.</summary>
    public string Name => LastFighterWins ? "FFA" : "2v2 Individual Stocks";

    /// <summary>What RespawnsRemaining becomes on a fighter's first death: the lives after this one.</summary>
    public int InitialRespawns => Lives - 1;

    /// <summary>Whether the match has been ended by these rules.</summary>
    public bool Ended { get; private set; }

    /// <summary>How many fighters are registered.</summary>
    public int FighterCount => _fighters.Count;

    /// <summary>How many fighters are out of lives.</summary>
    public int EliminatedCount => _fighters.Count(f => f.Eliminated);

    /// <summary>
    /// The rules for a match, or null when it has none: Free For All by its mode, Individual Stocks
    /// only when the server selected its mutator. Never in the Lab, whose mode can read "ffa" too
    /// but whose fighters must never stay down.
    /// </summary>
    public static StockRules? For(MatchSettings match)
    {
        if (match.MatchType == MatchSettings.LabMatchType)
        {
            return null;
        }

        if (IsFfa(match.Mode))
        {
            return new StockRules(LivesFromRingouts(match.NumRingouts));
        }

        return match.HasWorldBuff(IndividualStocksSlug) ? new StockRules(IndividualLivesFromRingouts(match.NumRingouts), lastFighterWins: false) : null;
    }

    /// <summary>
    /// FFA lives: the server's ringout count (a custom lobby sends its own, the queue sends the
    /// ringouts to win), or <see cref="DefaultLives"/> when it is not 1 to <see cref="MaxLives"/>.
    /// </summary>
    public static int LivesFromRingouts(int numRingouts) => numRingouts is >= 1 and <= MaxLives ? numRingouts : DefaultLives;

    /// <summary>
    /// Individual Stocks lives: half the ringouts to win, rounded up, so a team whose two fighters
    /// are both out has always given up the winning score (4 to win is 2 lives each).
    /// <see cref="DefaultIndividualLives"/> when the count is not 1 to <see cref="MaxLives"/>.
    /// </summary>
    public static int IndividualLivesFromRingouts(int numRingouts) => numRingouts is >= 1 and <= MaxLives ? (numRingouts + 1) / 2 : DefaultIndividualLives;

    /// <summary>Whether <paramref name="mode"/> is Free For All.</summary>
    public static bool IsFfa(string? mode) => string.Equals(mode, FfaMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records a fighter spawning into the match. Registering one again (a new game on the same
    /// game mode) puts it back in with full lives. Fighters beyond <see cref="MaxFighters"/> or
    /// with a player index outside it are not tracked.
    /// </summary>
    public void Register(nint fighter, int playerIndex, int team)
    {
        if (fighter == 0 || playerIndex is < 0 or >= MaxFighters)
        {
            return;
        }

        _fighters.RemoveAll(f => f.Pawn == fighter || f.PlayerIndex == playerIndex);
        if (_fighters.Count >= MaxFighters)
        {
            return;
        }

        _fighters.Add(new Fighter(fighter, playerIndex, team));
        _livesLeft[playerIndex] = Lives;
    }

    /// <summary>Whether <paramref name="pawn"/> is a registered fighter.</summary>
    public bool IsFighter(nint pawn) => _fighters.Any(f => f.Pawn == pawn);

    /// <summary>
    /// A registered fighter died, with <paramref name="respawnsRemaining"/> as its RespawnsRemaining
    /// after the first-death setup: that many lives are left. At 0 it is out. With
    /// <see cref="LastFighterWins"/>, returns the team that wins when this death leaves one fighter
    /// standing (and marks the match ended); otherwise null.
    /// </summary>
    public int? Died(nint fighter, int respawnsRemaining)
    {
        var dead = _fighters.Find(f => f.Pawn == fighter);
        if (dead == null)
        {
            return null;
        }

        _livesLeft[dead.PlayerIndex] = Math.Max(respawnsRemaining, 0);
        if (respawnsRemaining == 0)
        {
            dead.Eliminated = true;
        }

        if (!LastFighterWins || Ended || _fighters.Count < 2 || EliminatedCount < _fighters.Count - 1)
        {
            return null;
        }

        var survivor = _fighters.Find(f => !f.Eliminated);
        if (survivor == null)
        {
            return null;
        }

        Ended = true;
        return survivor.Team;
    }

    /// <summary>Whether the game's respawn of <paramref name="pawn"/> must be skipped: a registered fighter with no respawns left.</summary>
    public bool SkipsRespawn(nint pawn, int respawnsRemaining) => respawnsRemaining == 0 && IsFighter(pawn);

    /// <summary>The lives each player index has left, for the HUD.</summary>
    public int[] LivesLeft() => (int[])_livesLeft.Clone();

    /// <summary>The player indexes of the registered fighters, in registration order.</summary>
    public IEnumerable<int> PlayerIndexes => _fighters.Select(f => f.PlayerIndex);

    private sealed class Fighter(nint pawn, int playerIndex, int team)
    {
        public nint Pawn { get; } = pawn;
        public int PlayerIndex { get; } = playerIndex;
        public int Team { get; } = team;
        public bool Eliminated { get; set; }
    }
}
