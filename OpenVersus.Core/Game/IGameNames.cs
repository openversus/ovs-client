namespace OpenVersus.Game;

/// <summary>
/// The engine's name table, as the object finder needs it: whether it can be asked yet, an
/// FName for a string, and the string behind an FName. The game answers through its own
/// functions; a test answers from a dictionary.
/// </summary>
public interface IGameNames
{
    /// <summary>Whether <see cref="Find"/> and <see cref="ToString(FName)"/> may be called yet.</summary>
    bool Ready { get; }

    /// <summary>The existing FName for <paramref name="text"/>; Index 0 when there is none.</summary>
    FName Find(string text);

    /// <summary>The text of <paramref name="name"/>, or null when it cannot be read.</summary>
    string? ToString(FName name);
}

/// <summary>The engine's own name table, through the functions UeFunctionHooks resolves.</summary>
public sealed class EngineNames : IGameNames
{
    /// <summary>The only instance.</summary>
    public static EngineNames Instance { get; } = new();

    private EngineNames()
    {
    }

    /// <inheritdoc/>
    public bool Ready => UE.Ready && Engine.IsUp;
    /// <inheritdoc/>
    public FName Find(string text) => UE.FindName(text);
    /// <inheritdoc/>
    public string? ToString(FName name) => UE.NameToString(name);
}
