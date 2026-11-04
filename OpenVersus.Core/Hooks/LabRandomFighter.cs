using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// The Lab (and local play) with the Random fighter picked: Shaggy instead. Random (the OVS pak's character_ovs_random) is
/// a pick the server turns into a real fighter when it builds an online match; the Lab is built by the game alone, which
/// spawned the "?" itself, a character with no fighter, so nobody spawned and the player was stuck (2026-11-04).
/// <para>
/// UMvsLocalPlaySetupData::BeginMatch starts the Lab or a local match from the setup's players (Players, a
/// TMap&lt;int32, FGameplayPlayerData&gt;): entering the Lab and changing fighter inside it both come through it (a logging
/// build saw both, 2026-11-04; the local fighter picker's own Random check never ran). It is a Blueprint-callable function,
/// reached only through its exec thunk (Steam: 0x023AC160, jmp to 0x027876C0; Epic Games Store: 0x0239A2E0, jmp to
/// 0x025BD510); both builds read Players at +0x1D8. Before it runs, a player whose Character is the Random fighter gets
/// Shaggy and Shaggy's default skin.
/// </para>
/// <para>
/// Nothing here reads a name through a pointer taken from the map: the three assets are found once by name in the object
/// array, and the map's characters are compared with them by address. Every read is a guarded read (an unmapped address is
/// a false, never a fault); the map's counts are bounded; when anything is missing nothing is written; the game's
/// BeginMatch always runs.
/// </para>
/// </summary>
public static unsafe class LabRandomFighter
{
    /// <summary>
    /// Steam's UMvsLocalPlaySetupData::BeginMatch (0x027876C0): push rbx, rbp, rdi, r12, r13; sub rsp, 40h; the vtable call
    /// at 0x188; the call to its subsystem getter as a wildcard. Once in the Steam build, never in Epic's.
    /// </summary>
    internal const string BeginMatchPattern = "40 53 55 57 41 54 41 55 48 83 EC 40 48 8B 01 48 8B E9 FF 90 88 01 00 00 48 8B B8 C8 01 00 00 E8 ? ? ? ? 45 33 E4";
    /// <summary>push rbx; push rbp; push rdi; push r12; push r13: the instructions the entry hook moves (Steam).</summary>
    internal static readonly byte[] s_beginMatchPrologue = [0x40, 0x53, 0x55, 0x57, 0x41, 0x54, 0x41, 0x55];
    /// <summary>
    /// The Epic Games Store's BeginMatch (0x025BD510): rbx, rbp, rsi saved to the home space, push rdi; sub rsp, 40h; the
    /// same vtable call and getter. Once in the Epic build, never in Steam's.
    /// </summary>
    internal const string EpicBeginMatchPattern = "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 48 83 EC 40 48 8B 01 48 8B F9 FF 90 88 01 00 00 48 8B A8 C8 01 00 00 E8 ? ? ? ? 33 F6";
    /// <summary>mov [rsp+10h], rbx: the instruction the entry hook moves (Epic).</summary>
    internal static readonly byte[] s_epicBeginMatchPrologue = [0x48, 0x89, 0x5C, 0x24, 0x10];

    /// <summary>UMvsLocalPlaySetupData: TMap&lt;int32, FGameplayPlayerData&gt; Players (CXXHeaderDump; both builds' BeginMatch read it here).</summary>
    internal const int SetupDataPlayers = 0x1D8;
    /// <summary>
    /// One element of that map's sparse array: TPair&lt;int32, FGameplayPlayerData&gt; (the key, padding, the 0x158-byte
    /// value at 8) and the set's HashNextId and HashIndex.
    /// </summary>
    internal const int PlayerElementSize = 8 + Mvs.GameplayPlayerDataSize + 8;
    /// <summary>Where the FGameplayPlayerData sits in an element.</summary>
    internal const int PlayerElementValue = 8;
    /// <summary>FGameplayPlayerData: UCharacterData* Character.</summary>
    internal const int GameplayPlayerDataCharacter = 0x30;
    /// <summary>FGameplayPlayerData: USkinData* Skin.</summary>
    internal const int GameplayPlayerDataSkin = 0x38;
    /// <summary>
    /// The sparse array's allocation flags, a TBitArray after the elements' TArray: four inline uint32, the heap pointer
    /// (used when not null), NumBits.
    /// </summary>
    internal const int AllocationFlagsInline = 0x10;
    internal const int AllocationFlagsHeap = 0x20;
    internal const int AllocationFlagsNumBits = 0x28;
    /// <summary>More players than the Lab or local play ever has: a larger count is not a players map.</summary>
    internal const int MaxPlayers = 16;

    /// <summary>The three assets, as package and object name (Shaggy's default skin, slug skin_shaggy_default, is the object "Shaggy").</summary>
    internal static readonly (string Package, string Name) RandomCharacter = ("/OVS/Rewards/Characters/Random/character_ovs_random", "character_ovs_random");
    internal static readonly (string Package, string Name) StandInCharacter = ("/Game/Panda_Main/Characters/Shaggy/character_shaggy", "character_shaggy");
    internal static readonly (string Package, string Name) StandInSkin = ("/Game/Panda_Main/Characters/Shaggy/Skins/Shaggy", "Shaggy");

    private static delegate* unmanaged<nint, void> s_beginMatch;
    private static ILogger? s_log;
    private static ObjectFinder? s_finder;
    private static Assets? s_assets;
    private static bool s_reportedMissing;
    private static nint s_config;

    /// <summary>One asset as it was found: its address, class, name and outer (its package), all checked again before each use.</summary>
    private readonly record struct Asset(nint Address, nint Class, FName Name, nint Outer)
    {
        public static Asset Of(ObjectHeader h) => new(h.Address, h.ClassPrivate, h.Name, h.Outer);
    }

    /// <summary>The Random fighter and its stand-in. Each use works on its own copy, so nothing it holds changes under it.</summary>
    private readonly record struct Assets(Asset Random, Asset Shaggy, Asset Skin);

    /// <summary>Gives the hook the object finder it looks the assets up with.</summary>
    public static void Attach(ObjectFinder finder) => s_finder = finder;

    /// <summary>
    /// Hooks BeginMatch (the running build's). False when the pattern is missing; throws a <see cref="PatchException"/> when
    /// this build's code is not what the disassembly found.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        c.Log.Info("==Lab Random Fighter==");
        bool epic = GameBuild.Current.IsEpic;
        var hit = c.Patterns.Find("LabBeginMatch", epic ? EpicBeginMatchPattern : BeginMatchPattern);
        if (!hit.Found)
        {
            c.Log.Error("Lab random fighter: UMvsLocalPlaySetupData::BeginMatch was not found; the Random fighter will not spawn in the Lab");
            return false;
        }

        nint gateway = EntryHook.Install(hit.Address, epic ? s_epicBeginMatchPrologue : s_beginMatchPrologue, (nint)(delegate* unmanaged<nint, void>)&BeginMatch);
        s_beginMatch = (delegate* unmanaged<nint, void>)gateway;
        GameFunctions.Register("UMvsLocalPlaySetupData::BeginMatch", gateway, FunctionSource.Pattern,
            $"{hit.Name} at 0x{hit.Address:X}, entry hooked; this is the gateway to the original", "void UMvsLocalPlaySetupData::BeginMatch(UMvsLocalPlaySetupData* this)", c.Image);
        c.Log.Success($"Lab random fighter hooked (BeginMatch at 0x{hit.Address:X}): the Random fighter plays Shaggy in the Lab and local play");
        return true;
    }

    [UnmanagedCallersOnly]
    private static void BeginMatch(nint setup)
    {
        HookGuard.Run("LabRandomFighter", setup, static s => ReplaceRandom(s));
        s_beginMatch(setup);
        // The picker in the Lab writes the match config before BeginMatch; anything BeginMatch copies there, after.
        HookGuard.Run("LabRandomFighter", 0, static _ =>
        {
            if (Resolve(ProcessMemory.Instance) is { } assets)
            {
                ReplaceRandomInConfig(assets);
            }
        });
    }

    private static void ReplaceRandom(nint setup)
    {
        var memory = ProcessMemory.Instance;
        if (Resolve(memory) is not { } assets)
        {
            return;
        }

        Replace(RandomPlayers(memory, setup, assets.Random.Address), assets, "setup");
        ReplaceRandomInConfig(assets);
    }

    /// <summary>
    /// The match config (UMvsGameplayConfig, a game-instance subsystem): its Players array is what the fighters are spawned
    /// from (a logging build saw the Lab's picker leave Random there while the setup held Shaggy, 2026-11-04).
    /// </summary>
    private static void ReplaceRandomInConfig(Assets assets)
    {
        if (s_finder is not { } finder)
        {
            return;
        }

        nint configClass = finder.FindClass("MvsGameplayConfig");
        if (s_config == 0 || !finder.IsLiveOf(s_config, configClass))
        {
            s_config = finder.FindInstanceOfClass(configClass);
        }

        if (s_config != 0)
        {
            Replace(ConfigRandomPlayers(ProcessMemory.Instance, s_config, assets.Random.Address), assets, "match config");
        }
    }

    private static void Replace(List<nint> players, Assets assets, string where)
    {
        foreach (nint player in players)
        {
            // Skin first, then the character; when the character cannot be written the old skin goes back, so a player
            // is either untouched or Shaggy in his skin, never Random in Shaggy's skin or Shaggy in the "?" skin.
            if (!CodeWriter.TryRead(player + GameplayPlayerDataSkin, out nint oldSkin))
            {
                s_log?.Error($"Lab random fighter: the player at 0x{player:X} could not be read");
                continue;
            }

            bool skinWritten = CodeWriter.TryWrite(player + GameplayPlayerDataSkin, assets.Skin.Address);
            if (skinWritten && CodeWriter.TryWrite(player + GameplayPlayerDataCharacter, assets.Shaggy.Address))
            {
                s_log?.Success($"Lab random fighter: a player picked Random ({where}); plays {StandInCharacter.Name} in {StandInSkin.Package}");
            }
            else
            {
                if (skinWritten)
                {
                    CodeWriter.TryWrite(player + GameplayPlayerDataSkin, oldSkin);
                }

                s_log?.Error($"Lab random fighter: the player at 0x{player:X} could not be changed; left as it was");
            }
        }
    }

    /// <summary>
    /// The FGameplayPlayerData of every player in <paramref name="setup"/>'s Players whose Character is
    /// <paramref name="random"/>. Empty when the map does not read as one: an unreadable or empty element array, a count
    /// over <see cref="MaxPlayers"/>, a count over its own capacity or over the allocation flags' bits.
    /// </summary>
    internal static List<nint> RandomPlayers(IMemory memory, nint setup, nint random)
    {
        var found = new List<nint>();
        nint players = setup + SetupDataPlayers;
        if (random == 0 || !memory.TryRead(players, out TArrayHeader elements) || elements.Data == 0
            || elements.Count is <= 0 or > MaxPlayers || elements.Max < elements.Count
            || !memory.TryRead(players + AllocationFlagsHeap, out nint heapBits)
            || !memory.TryRead(players + AllocationFlagsNumBits, out int numBits) || numBits < elements.Count)
        {
            return found;
        }

        nint bits = heapBits != 0 ? heapBits : players + AllocationFlagsInline;
        // MaxPlayers fits in the first word of the flags.
        if (!memory.TryRead(bits, out uint allocated))
        {
            return found;
        }

        for (int i = 0; i < elements.Count; i++)
        {
            nint player = elements.Data + i * PlayerElementSize + PlayerElementValue;
            if ((allocated & (1u << i)) != 0 && memory.TryRead(player + GameplayPlayerDataCharacter, out nint character) && character == random)
            {
                found.Add(player);
            }
        }

        return found;
    }

    /// <summary>
    /// The FGameplayPlayerData of every player in the match config's Players (a TArray&lt;FGameplayPlayerData&gt; at
    /// <see cref="Mvs.GameplayConfigPlayers"/>) whose Character is <paramref name="random"/>. Empty when the array does not
    /// read as one: unreadable, empty, more than <see cref="MaxPlayers"/>, or a count over its capacity.
    /// </summary>
    internal static List<nint> ConfigRandomPlayers(IMemory memory, nint config, nint random)
    {
        var found = new List<nint>();
        if (random == 0 || config == 0 || !memory.TryRead(config + Mvs.GameplayConfigPlayers, out TArrayHeader players) || players.Data == 0
            || players.Count is <= 0 or > MaxPlayers || players.Max < players.Count)
        {
            return found;
        }

        for (int i = 0; i < players.Count; i++)
        {
            nint player = players.Data + i * Mvs.GameplayPlayerDataSize;
            if (memory.TryRead(player + GameplayPlayerDataCharacter, out nint character) && character == random)
            {
                found.Add(player);
            }
        }

        return found;
    }

    /// <summary>
    /// The three assets: found by name once, then each use checks they are still the same objects (class, name and outer at
    /// each address unchanged). Null, with one error in the log, when one is not loaded. BeginMatch runs on the game thread
    /// only; each caller works on the copy it is given.
    /// </summary>
    private static Assets? Resolve(IMemory memory)
    {
        if (s_assets is { } cached && Still(memory, cached.Random) && Still(memory, cached.Shaggy) && Still(memory, cached.Skin))
        {
            return cached;
        }

        s_assets = null;
        if (s_finder is not { } finder)
        {
            return null;
        }

        nint characterClass = finder.FindClass("CharacterData");
        var random = Named(finder, characterClass, RandomCharacter);
        var shaggy = Named(finder, characterClass, StandInCharacter);
        var skin = Named(finder, finder.FindClass("SkinData"), StandInSkin);
        if (random.Address == 0 || shaggy.Address == 0 || skin.Address == 0)
        {
            if (!s_reportedMissing)
            {
                s_reportedMissing = true;
                s_log?.Error($"Lab random fighter: not loaded: {(random.Address == 0 ? RandomCharacter.Package + " " : "")}{(shaggy.Address == 0 ? StandInCharacter.Package + " " : "")}{(skin.Address == 0 ? StandInSkin.Package : "")}; Random is left as it is");
            }

            return null;
        }

        var found = new Assets(Asset.Of(random), Asset.Of(shaggy), Asset.Of(skin));
        s_assets = found;
        return found;
    }

    private static bool Still(IMemory memory, Asset asset) =>
        ObjectHeader.TryRead(memory, asset.Address, out var h) && h.ClassPrivate == asset.Class && h.Outer == asset.Outer
        && h.Name.Index == asset.Name.Index && h.Name.Number == asset.Name.Number;

    /// <summary>
    /// The live instance of <paramref name="uclass"/> with this object name in this package (compared by FName: the object's
    /// name, and its outer's, which for an asset is its package).
    /// </summary>
    private static ObjectHeader Named(ObjectFinder finder, nint uclass, (string Package, string Name) asset)
    {
        if (uclass == 0)
        {
            return default;
        }

        FName name = finder.Names.Find(asset.Name);
        FName package = finder.Names.Find(asset.Package);
        if (name.Index == 0 || package.Index == 0)
        {
            return default;
        }

        foreach (nint obj in finder.FindInstancesOfClass(uclass))
        {
            if (ObjectHeader.TryRead(finder.Memory, obj, out var h) && h.Name.Index == name.Index && h.Name.Number == 0
                && ObjectHeader.TryRead(finder.Memory, h.Outer, out var outer) && outer.Name.Index == package.Index && outer.Name.Number == 0)
            {
                return h;
            }
        }

        return default;
    }
}
