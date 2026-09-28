using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// Stops the game's WB Analytics (WBA) telemetry from ever leaving the machine. Left alone, the game
/// posts every screen and click to event.wbinsights.com every 10 seconds, each event carrying the
/// player's OpenVersus session token, IP, Steam id and hardware id. Every 10 seconds WBA's flush takes
/// the queued events off the queue and hands them, as one batch, to its send function, which is the
/// only code that builds the multipart body and creates the HTTP request. The send function already
/// returns without sending when the batch serializes to nothing; this makes that return unconditional.
/// No request is created (so no DNS lookup, connection or retry), the batch is freed by its task as
/// usual, and the queue is still emptied every flush. The WBA provider itself stays: the game calls
/// into it directly and cannot run without it. The pattern lives here, not in OpenVersus.toml: this
/// is not something a player can switch off or point elsewhere.
/// </summary>
public static class TelemetryPatch
{
    /// <summary>The no-send branch in WBA's send function, found in the image at startup; the game's only build has it once.</summary>
    internal const string Pattern = "45 33 FF 48 8B F1 48 8B 09 4C 89 7D D7 4C 89 7D DF E8 ? ? ? ? 84 C0 0F 84 AA 02 00 00";
    /// <summary>Where the je (serialized nothing) is, from the start of the pattern.</summary>
    internal const int BranchOffset = 0x18;
    /// <summary>Where the send function's no-send return is, from the start of the pattern.</summary>
    internal const int ExitOffset = 0x2C8;

    /// <summary>je rel32 to the no-send return.</summary>
    internal static readonly byte[] Expected = [0x0F, 0x84, 0xAA, 0x02, 0x00, 0x00];
    /// <summary>jmp rel32 to the same return, then a NOP over the je's last byte.</summary>
    internal static readonly byte[] Jump = [0xE9, 0xAB, 0x02, 0x00, 0x00, 0x90];
    /// <summary>
    /// The start of the no-send return: mov rcx, [rbp-0x29] (the payload buffer); the saved r15, rdi
    /// and rsi restored; the buffer freed when there is one.
    /// </summary>
    internal static readonly byte[] Exit =
    [
        0x48, 0x8B, 0x4D, 0xD7,
        0x4C, 0x8B, 0xBC, 0x24, 0xC0, 0x00, 0x00, 0x00,
        0x48, 0x8B, 0xBC, 0x24, 0xF0, 0x00, 0x00, 0x00,
        0x48, 0x8B, 0xB4, 0x24, 0xE8, 0x00, 0x00, 0x00,
        0x48, 0x85, 0xC9, 0x74, 0x05,
    ];

    /// <summary>
    /// Makes the send function always take its no-send return. False when the pattern is missing;
    /// throws a <see cref="PatchException"/> when the je or the return it goes to are not the expected bytes.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==Telemetry==");
        var hit = c.Patterns.Find("Telemetry", Pattern);
        if (!hit.Found)
        {
            c.Log.Error("Telemetry: WBA's send function was not found; the game's telemetry is NOT stopped");
            return false;
        }

        CodeWriter.Expect(hit.Address + ExitOffset, Exit);
        nint site = hit.Address + BranchOffset;
        CodeWriter.WriteIf(site, Expected, Jump, code: true);
        c.Log.Success($"Telemetry: WBA's send function never sends (patched at 0x{site:X}); nothing is sent to WB");
        return true;
    }
}
