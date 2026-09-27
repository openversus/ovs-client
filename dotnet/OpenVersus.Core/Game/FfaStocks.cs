namespace OpenVersus.Game;

/// <summary>
/// The FFA stock rules for one match, without the game: every fighter has <see cref="Lives"/>
/// lives, a fighter out of lives stays down, and the match ends when one fighter is left.
/// <para>
/// The lives themselves are the game's own: APfgFixedPawn.RespawnsRemaining, which every peer
/// simulates alike (and rolls back alike). Online matches start it at -1 (unlimited); on a
/// fighter's first death it becomes <see cref="InitialRespawns"/>, the native DoRespawn counts it
/// down, and a death at 0 is the last. What this class keeps is only who is out, for ending the
/// match, and the lives each player has left, for the HUD.
/// </para>
/// </summary>
public sealed class FfaStocks
{
    /// <summary>The mode string the server sends for Free For All.</summary>
    public const string Mode = "FFA";
    /// <summary>Lives when the server sends no usable ringout count.</summary>
    public const int DefaultLives = 3;
    /// <summary>The most lives a match takes; a larger count is treated as unusable.</summary>
    public const int MaxLives = 99;
    /// <summary>The most fighters an FFA match has.</summary>
    public const int MaxFighters = 8;

    private readonly List<Fighter> _fighters = [];
    private readonly int[] _livesLeft = new int[MaxFighters];

    /// <summary>A match with <paramref name="lives"/> lives per fighter.</summary>
    public FfaStocks(int lives)
    {
        Lives = lives;
        Array.Fill(_livesLeft, lives);
    }

    /// <summary>Lives per fighter.</summary>
    public int Lives { get; }

    /// <summary>What RespawnsRemaining becomes on a fighter's first death: the lives after this one.</summary>
    public int InitialRespawns => Lives - 1;

    /// <summary>Whether the match has been ended by these rules.</summary>
    public bool Ended { get; private set; }

    /// <summary>How many fighters are registered.</summary>
    public int FighterCount => _fighters.Count;

    /// <summary>How many fighters are out of lives.</summary>
    public int EliminatedCount => _fighters.Count(f => f.Eliminated);

    /// <summary>
    /// The lives for a match: the server's ringout count (a custom lobby sends its own, the
    /// queue sends the ringouts to win), or <see cref="DefaultLives"/> when it is not 1 to <see cref="MaxLives"/>.
    /// </summary>
    public static int LivesFromRingouts(int numRingouts) => numRingouts is >= 1 and <= MaxLives ? numRingouts : DefaultLives;

    /// <summary>Whether <paramref name="mode"/> is Free For All.</summary>
    public static bool IsFfa(string? mode) => string.Equals(mode, Mode, StringComparison.OrdinalIgnoreCase);

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
    /// after the first-death setup: that many lives are left. At 0 it is out. Returns the team that
    /// wins when this death leaves one fighter standing (and marks the match ended), otherwise null.
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

        if (Ended || _fighters.Count < 2 || EliminatedCount < _fighters.Count - 1)
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
