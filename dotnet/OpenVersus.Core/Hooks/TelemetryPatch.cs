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

    // UFighterGameInstance::RecordEventWithAttributes(const FString& EventName, TArray<FPFGAnalyticsEventAttr>
    // Attributes), void. The Blueprint RecordEventWithAttributes calls it, and so do 14 places in the game's C++.
    // Attributes is passed by value, so the function frees it: every path ends in mov rcx, rbp (Attributes) and
    // a tail jump to the array's destructor. When the analytics object it looks up is null, it takes that path
    // straight away, before building or recording anything; that is the path made unconditional here.

    /// <summary>The start of RecordEventWithAttributes; unique in the game's only build.</summary>
    internal const string RecordPattern = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 40 49 8B E8 4C 8B F2";
    /// <summary>Where the je (no analytics object) is, from the start of the function.</summary>
    internal const int RecordBranchOffset = 0x6B;
    /// <summary>Where the path that only frees Attributes and returns starts, from the start of the function.</summary>
    internal const int RecordExitOffset = 0x105;

    /// <summary>je rel32 to the free-and-return path.</summary>
    internal static readonly byte[] RecordExpected = [0x0F, 0x84, 0x94, 0x00, 0x00, 0x00];
    /// <summary>jmp rel32 to the same path, then a NOP over the je's last byte.</summary>
    internal static readonly byte[] RecordJump = [0xE9, 0x95, 0x00, 0x00, 0x00, 0x90];
    /// <summary>
    /// The free-and-return path up to its tail jump: mov rcx, rbp (Attributes); the saved rbx, rbp, rsi and
    /// rdi restored; the frame released; r14 popped; jmp to the TArray destructor.
    /// </summary>
    internal static readonly byte[] RecordExit =
    [
        0x48, 0x8B, 0xCD,
        0x48, 0x8B, 0x5C, 0x24, 0x50,
        0x48, 0x8B, 0x6C, 0x24, 0x58,
        0x48, 0x8B, 0x74, 0x24, 0x60,
        0x48, 0x8B, 0x7C, 0x24, 0x68,
        0x48, 0x83, 0xC4, 0x40,
        0x41, 0x5E,
        0xE9,
    ];

    /// <summary>
    /// Makes RecordEventWithAttributes free what it is given and return without recording it. False when
    /// the pattern is missing; throws a <see cref="PatchException"/> when the je or the path it goes to are
    /// not the expected bytes.
    /// </summary>
    public static bool ApplyRecord(HookContext c)
    {
        c.Log.Info("==TelemetryRecord==");
        var hit = c.Patterns.Find("TelemetryRecord", RecordPattern);
        if (!hit.Found)
        {
            c.Log.Error("TelemetryRecord: RecordEventWithAttributes was not found; its events are still recorded (and not sent)");
            return false;
        }

        CodeWriter.Expect(hit.Address + RecordExitOffset, RecordExit);
        nint site = hit.Address + RecordBranchOffset;
        CodeWriter.WriteIf(site, RecordExpected, RecordJump, code: true);
        c.Log.Success($"TelemetryRecord: RecordEventWithAttributes records nothing (patched at 0x{site:X})");
        return true;
    }

    /// <summary>A function made to return at once: the pattern is its first 32 bytes, unique in the game's only build.</summary>
    /// <param name="Name">For the log.</param>
    /// <param name="Pattern">The function's first 32 bytes.</param>
    internal sealed record EntryReturn(string Name, string Pattern);

    /// <summary>ret, over the function's first byte.</summary>
    internal const byte Ret = 0xC3;

    /// <summary>
    /// The Store's analytics, outside the record and send layers. Every one is void and owns nothing its
    /// caller does not free (an enum, const references, or no arguments), so returning at once is all there
    /// is to it. The six interface implementations are only reached through their vtables; the two
    /// recorders only from their Blueprint thunks and those implementations. Each ends up in
    /// RecordEventWithAttributes, which is also cut.
    /// </summary>
    internal static readonly EntryReturn[] ShopFunctions =
    [
        // IMvsShopAnalyticsGameUiInteractable::OnMvsShopAnalyticsInteraction(EMvsShopAnalyticsInteracton), per widget.
        new("the Store tile's interaction (MvsShopItemCellWidget)", "40 53 48 81 EC E0 00 00 00 48 8D 99 D8 FC FF FF 44 0F B6 C2 48 8B D3 48 8D 4C 24 20 E8 3F 96 BA"),
        new("the product page's interaction (MvsShopItemDetailViewWidget)", "40 53 48 81 EC E0 00 00 00 48 8D 99 B8 FB FF FF 44 0F B6 C2 48 8B D3 48 8D 4C 24 20 E8 EF 40 BA"),
        new("the purchase dialog's interaction (MvsShopPurchaseModalWidget)", "40 53 48 81 EC E0 00 00 00 48 8D 99 C8 FB FF FF 44 0F B6 C2 48 8B D3 48 8D 4C 24 20 E8 2F FF B9"),
        // IMvsShopAnalyticsGameStore::OnMvsShopAnalyticsEnter/Exit(const FString&...), the interface's own, shared by every screen.
        new("entering a Store screen (game_store_enter)", "48 89 5C 24 10 48 89 6C 24 18 56 57 41 56 48 81 EC D0 00 00 00 48 8B D9 49 8B F1 48 8D 8C 24 F0"),
        new("leaving a Store screen (game_store_exit)", "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 41 56 48 81 EC A0 00 00 00 48 8B 01 49 8B D8 48 8B"),
        // IMvsShopAnalyticsGameStoreUi::OnMvsShopAnalyticsGameStoreUiOpen(), per widget.
        new("the product page opening (MvsShopItemDetailViewWidget)", "40 53 48 81 EC 90 00 00 00 48 8D 99 B0 FB FF FF 48 8B D3 48 8D 4C 24 20 E8 93 3E BA FF 48 8B 03"),
        new("the Store opening (MvsShopWidget)", "48 89 5C 24 08 57 48 81 EC 90 00 00 00 48 8D 99 E8 FA FF FF 48 8B CB E8 24 83 30 00 48 8B D3 48"),
        // UMvsShopAnalytics::RecordGameStoreUiInteract/Open(const FMvs...Attributes&, UFighterGameInstance*), static.
        new("RecordGameStoreUiInteract (game_store_ui_interact)", "48 89 5C 24 08 57 48 83 EC 40 48 8B FA 48 8B D1 48 8D 4C 24 20 E8 A6 65 FF FF 48 8D 15 37 C5 7F"),
        new("RecordGameStoreUiOpen (game_store_ui_open)", "48 89 5C 24 08 57 48 83 EC 40 48 8B FA 48 8B D1 48 8D 4C 24 20 E8 B6 6E FF FF 48 8D 15 0F C5 7F"),
    ];

    /// <summary>
    /// Makes each of <see cref="ShopFunctions"/> return at once. True only when every one was patched; each
    /// one missing or not as expected is logged and the rest are still patched.
    /// </summary>
    public static bool ApplyShop(HookContext c)
    {
        c.Log.Info("==TelemetryShop==");
        int patched = 0;
        foreach (var f in ShopFunctions)
        {
            var hit = c.Patterns.Find("TelemetryShop", f.Pattern);
            if (!hit.Found)
            {
                c.Log.Error($"TelemetryShop: {f.Name} was not found; not patched");
                continue;
            }

            try
            {
                CodeWriter.WriteIf(hit.Address, BytePattern.Parse(f.Pattern).Bytes, [Ret], code: true);
                patched++;
            }
            catch (PatchException e)
            {
                c.Log.Error($"TelemetryShop: {f.Name}: {e.Message}");
            }
        }

        c.Log.Success($"TelemetryShop: {patched} of {ShopFunctions.Length} Store analytics functions return at once");
        return patched == ShopFunctions.Length;
    }
}
