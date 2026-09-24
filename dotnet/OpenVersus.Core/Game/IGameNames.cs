namespace OpenVersus.Game;

/// <summary>
/// The engine's name table, as the object finder needs it: whether it can be asked yet, an
/// FName for a string, and the string behind an FName. The game answers through its own
/// functions; a test answers from a dictionary.
/// </summary>
public interface IGameNames
{
    bool Ready { get; }

    /// <summary>The existing FName for <paramref name="text"/>; Index 0 when there is none.</summary>
    FName Find(string text);

    string? ToString(FName name);
}

/// <summary>The engine's own name table, through the functions UeFunctionHooks resolves.</summary>
public sealed class EngineNames : IGameNames
{
    public static EngineNames Instance { get; } = new();

    private EngineNames()
    {
    }

    public bool Ready => UE.Ready && Engine.IsUp;
    public FName Find(string text) => UE.FindName(text);
    public string? ToString(FName name) => UE.NameToString(name);
}
