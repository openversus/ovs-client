namespace OpenVersus.Game;

/// <summary>
/// The two builds of the game's final version: Steam's and the Epic Games Store's. They are the same code compiled
/// apart (PE timestamps 0x67DB021B and 0x67DAFDD4), so almost every pattern matches both; what does not is here: where
/// a few globals and functions sit, and the hooks that pick an Epic pattern or prologue by <see cref="IsEpic"/>. The
/// object array's layout and XOR key are the same in both (FUObjectArray::FreeUObjectIndex, Steam 0x2D4A570 and Epic
/// 0x2D46FC0, reads the same offsets and the same key). Each Epic address was found from the Steam one through the
/// code that uses it, matched in both builds with its own addresses as wildcards (2026-10-29).
/// </summary>
public sealed record GameBuild(string Name, uint ObjectArrayRva, uint ProcessEventRva, uint UFunctionVTableNativeRva, uint UFunctionVTableBlueprintRva)
{
    /// <summary>The .text hash of Steam's MultiVersus-Win64-Shipping.exe (<see cref="GameImage.HashTextSection"/>).</summary>
    public const ulong SteamTextHash = 0x3957B487D6644832;
    /// <summary>The .text hash of the Epic Games Store's MultiVersus-Win64-Shipping.exe.</summary>
    public const ulong EpicTextHash = 0x939CB42ED96A0548;

    /// <summary>Steam's build, the one every RVA in this client was first read from.</summary>
    public static readonly GameBuild Steam = new("Steam", ObjectArray.GUObjectArrayRva, GameFunctions.ProcessEventRva,
        Mvs.UFunctionVTableNativeRva, Mvs.UFunctionVTableBlueprintRva);

    /// <summary>The Epic Games Store's build.</summary>
    public static readonly GameBuild Epic = new("Epic Games Store", 0x081BC890, 0x02D3A260, 0x0678DE90, 0x0678DB00);

    /// <summary>The build this game runs, chosen by <see cref="Select"/> before any hook is applied; Steam's until then.</summary>
    public static GameBuild Current { get; private set; } = Steam;

    /// <summary>Whether this is the Epic Games Store's build.</summary>
    public bool IsEpic => ReferenceEquals(this, Epic);

    // push rbp; push rsi; push rdi; push r12: UObject::ProcessEvent's first instructions, the same in both builds.
    private static readonly byte[] s_processEventStart = [0x40, 0x55, 0x56, 0x57, 0x41, 0x54];

    /// <summary>
    /// The build of <paramref name="image"/>, by its .text hash; an unknown hash (a hook that patched .text first, say)
    /// is told by where ProcessEvent starts. Steam's when neither says Epic, as before there was a choice.
    /// </summary>
    public static GameBuild Select(ulong textHash, GameImage image)
    {
        Current = textHash switch
        {
            SteamTextHash => Steam,
            EpicTextHash => Epic,
            _ => StartsProcessEvent(image, Epic.ProcessEventRva) && !StartsProcessEvent(image, Steam.ProcessEventRva) ? Epic : Steam,
        };
        return Current;
    }

    private static bool StartsProcessEvent(GameImage image, uint rva) =>
        rva + (uint)s_processEventStart.Length <= (uint)image.Size && image.Bytes.Slice((int)rva, s_processEventStart.Length).SequenceEqual(s_processEventStart);
}
