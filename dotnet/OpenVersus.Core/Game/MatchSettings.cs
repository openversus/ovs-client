using OpenVersus.Memory;

namespace OpenVersus.Game;

/// <summary>
/// What the server sent for the match being played, from UMvsGameplayConfig: the mode, the ringout
/// count and the mutators (world buffs) selected for it. Read once per match, when the game mode
/// registers its first character; every peer reads the same config, so every peer turns the same
/// rules on.
/// </summary>
/// <param name="Mode">ModeString, such as "1v1", "2v2" or "FFA".</param>
/// <param name="NumRingouts">FCustomGameSettings.NumRingouts: the ringouts to win, or a custom lobby's own count.</param>
/// <param name="WorldBuffs">The slug of each world buff in WorldBuffs, in order; "" for one whose slug cannot be read.</param>
/// <param name="MatchType">EMvsMatchType as a number (5 is a custom lobby).</param>
/// <param name="Online">bIsOnlineMatch.</param>
public sealed record MatchSettings(string Mode, int NumRingouts, IReadOnlyList<string> WorldBuffs, int MatchType, bool Online)
{
    /// <summary>The most world buffs read; a count above it means the array is not what it should be.</summary>
    public const int MaxWorldBuffs = 64;

    /// <summary>Whether the mutator with <paramref name="slug"/> was selected (any case).</summary>
    public bool HasWorldBuff(string slug) => WorldBuffs.Any(b => string.Equals(b, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line for the log.</summary>
    public override string ToString() =>
        $"mode={Mode} ringouts={NumRingouts} matchType={MatchType} online={Online} worldBuffs=[{string.Join(", ", WorldBuffs)}]";

    /// <summary>
    /// Reads the config at <paramref name="config"/>. False when the mode cannot be read; an
    /// unreadable world buff array reads as none, and an unreadable slug as "".
    /// </summary>
    public static bool TryRead(IMemory memory, nint config, out MatchSettings settings)
    {
        settings = null!;
        if (config == 0 || !GameStrings.TryReadFString(memory, config + Mvs.GameplayConfigModeString, out string mode))
        {
            return false;
        }

        memory.TryRead(config + Mvs.GameplayConfigNumRingouts, out int ringouts);
        memory.TryRead(config + Mvs.GameplayConfigMatchType, out byte matchType);
        memory.TryRead(config + Mvs.GameplayConfigIsOnlineMatch, out byte online);
        settings = new MatchSettings(mode, ringouts, ReadWorldBuffs(memory, config), matchType, online != 0);
        return true;
    }

    private static List<string> ReadWorldBuffs(IMemory memory, nint config)
    {
        var slugs = new List<string>();
        // TArray<UMvsMetaWorldBuffHsda*>: data pointer, then the count.
        if (!memory.TryRead(config + Mvs.GameplayConfigWorldBuffs, out nint data)
            || !memory.TryRead(config + Mvs.GameplayConfigWorldBuffs + 8, out int count)
            || data == 0 || count is <= 0 or > MaxWorldBuffs)
        {
            return slugs;
        }

        for (int i = 0; i < count; i++)
        {
            string slug = "";
            if (memory.TryRead(data + i * nint.Size, out nint asset) && asset != 0)
            {
                GameStrings.TryReadFString(memory, asset + Mvs.HydraSyncDataAssetSlug, out slug);
            }

            slugs.Add(slug ?? "");
        }

        return slugs;
    }
}
