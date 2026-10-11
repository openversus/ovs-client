using OpenVersus.Game;
using OpenVersus.Memory;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Stops 1v1 and 2v2 queues from giving up on players after about two minutes. When a ticket is
/// submitted the game arms a one-shot timer (115 s plus up to 20 s at random) that cancels the
/// player's own ticket and puts them in a bot match instead; for 1v1 and 2v2 it arms that timer only
/// while the console variable PFG.PvPBots ("PvP Bots Enabled", default 1) is non-zero. Casual and
/// arena arm theirs without looking at it, so they keep their bot fallback. This sets the variable
/// to 0 once the game is up; the game then also sends "NoBots": true with each matchmaking request.
/// The pattern lives here, not in OpenVersus.toml.
/// </summary>
public static unsafe class PvPBotsPatch
{
    /// <summary>
    /// The 1v1/2v2 gate in the timer setup: <c>test dil, dil; jmp; mov rax, [PFG.PvPBots's int*]; cmp dword [rax], 0; je</c>
    /// (no timer); each build (Steam, Epic Games Store) has it once.
    /// </summary>
    internal const string Pattern = "40 84 FF EB 0A 48 8B 05 ? ? ? ? 83 38 00 0F 84";
    /// <summary>Where the <c>mov rax, [rip + disp32]</c> is, from the start of the pattern.</summary>
    internal const int LoadOffset = 5;
    /// <summary>The length of that mov.</summary>
    private const int LoadLength = 7;
    /// <summary>How long to wait for the game to come up before setting the variable anyway.</summary>
    private const int WaitMs = 60_000;

    private static nint s_slot;

    /// <summary>
    /// Finds the global holding PFG.PvPBots's value pointer. False when the pattern is missing; the value
    /// is set later, by <see cref="Run"/>.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==PvPBots==");
        var hit = c.Patterns.Find("PvPBots", Pattern);
        if (!hit.Found)
        {
            c.Log.Error("PvPBots: the 1v1/2v2 bot timer's gate was not found; queues will still switch to bots");
            return false;
        }

        s_slot = CallSite.Destination(hit.Address + LoadOffset, displacementOffset: 3, instructionLength: LoadLength);
        c.Log.Debug($"PvPBots: gate at 0x{hit.Address:X}, value pointer held at 0x{s_slot:X}");
        return true;
    }

    /// <summary>
    /// Waits for the game to come up (by then the engine has applied any ini settings), logs the value it
    /// found, and sets it to 0. Runs off the game thread; the game reads the value each time it arms a timer.
    /// </summary>
    public static void Run(ILogger log)
    {
        if (s_slot == 0)
        {
            return;
        }

        long deadline = Environment.TickCount64 + WaitMs;
        while (!Engine.IsUp && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(500);
        }

        if (!CodeWriter.TryRead(s_slot, out nint value) || value == 0 || !CodeWriter.TryRead(value, out int before))
        {
            log.Error($"PvPBots: PFG.PvPBots's value could not be read (pointer at 0x{s_slot:X} holds 0x{value:X}); queues will still switch to bots");
            return;
        }

        Interlocked.Exchange(ref *(int*)value, 0);
        log.Success($"PvPBots: PFG.PvPBots was {before}, now 0 (at 0x{value:X}); 1v1 and 2v2 queues never switch to bots");
    }
}
