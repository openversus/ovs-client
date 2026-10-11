using System.Buffers.Binary;
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
public static unsafe class TelemetryPatch
{
    /// <summary>
    /// The no-send branch in WBA's send function, found in the image at startup; each build has it once. The
    /// two rbp offsets (the payload buffer's slot and the one after it, both zeroed) and the je's distance are
    /// the build's own: the Epic Games Store build's send function has a smaller frame and fewer bytes.
    /// </summary>
    internal const string Pattern = "45 33 FF 48 8B F1 48 8B 09 4C 89 7D ? 4C 89 7D ? E8 ? ? ? ? 84 C0 0F 84 ? ? 00 00";
    /// <summary>Where the disp8 of <c>mov [rbp + X], r15</c> (the payload buffer's slot) is, from the start of the pattern.</summary>
    internal const int PayloadSlotOffset = 0x0C;
    /// <summary>Where the je (serialized nothing) is, from the start of the pattern.</summary>
    internal const int BranchOffset = 0x18;
    /// <summary>How far past the match the no-send return may lie; both builds' send functions are under 0x300 bytes.</summary>
    internal const int Reach = 0x1000;

    /// <summary>
    /// The start of the no-send return: mov rcx, [rbp + X] (the payload buffer); the saved r15, rdi and rsi
    /// restored from the frame; the buffer freed when there is one. X and the frame offsets are the build's.
    /// </summary>
    internal static readonly BytePattern ExitShape = BytePattern.Parse(
        "48 8B 4D ? 4C 8B BC 24 ? 00 00 00 48 8B BC 24 ? 00 00 00 48 8B B4 24 ? 00 00 00 48 85 C9 74 05");

    /// <summary>What the patch at a match comes to.</summary>
    /// <param name="ExitOffset">Where the no-send return is, from the start of the match.</param>
    /// <param name="Je">The je rel32 as found: what the site must still hold to be patched.</param>
    /// <param name="Jump">jmp rel32 to the same return, then a NOP over the je's last byte.</param>
    internal readonly record struct Plan(int ExitOffset, byte[] Je, byte[] Jump);

    /// <summary>
    /// Reads the je at <see cref="BranchOffset"/> of <paramref name="function"/> (the bytes from a match on)
    /// and checks the return it goes to: the <see cref="ExitShape"/>, freeing the very slot the function
    /// zeroed at the start. Throws a <see cref="PatchException"/> when any of that is not so.
    /// </summary>
    internal static Plan PlanFor(ReadOnlySpan<byte> function)
    {
        if (function.Length < BranchOffset + 6 || function[BranchOffset] != 0x0F || function[BranchOffset + 1] != 0x84)
        {
            throw new PatchException("no je rel32 after the serialize call; not patching");
        }

        int rel = BinaryPrimitives.ReadInt32LittleEndian(function[(BranchOffset + 2)..]);
        int exit = BranchOffset + 6 + rel;
        if (rel <= 0 || exit + ExitShape.Length > function.Length)
        {
            throw new PatchException($"the je goes 0x{rel:X} ahead, outside what was read; not patching");
        }

        if (!ExitShape.MatchesAt(function, exit))
        {
            throw new PatchException($"expected {ExitShape} at the je's target, found {Convert.ToHexString(function.Slice(exit, ExitShape.Length))}; not patching");
        }

        if (function[exit + 3] != function[PayloadSlotOffset])
        {
            throw new PatchException($"the return frees [rbp + 0x{function[exit + 3]:X}], the function zeroed [rbp + 0x{function[PayloadSlotOffset]:X}]; not patching");
        }

        var jump = new byte[6];
        jump[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(jump.AsSpan(1), rel + 1);
        jump[5] = 0x90;
        return new Plan(exit, function.Slice(BranchOffset, 6).ToArray(), jump);
    }

    /// <summary>
    /// Makes the send function always take its no-send return. False when the pattern is missing;
    /// throws a <see cref="PatchException"/> when the je or the return it goes to are not as expected.
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

        int reach = (int)Math.Min(Reach, c.Image.Base + c.Image.Size - hit.Address);
        var plan = PlanFor(new ReadOnlySpan<byte>((void*)hit.Address, reach));
        nint site = hit.Address + BranchOffset;
        CodeWriter.WriteIf(site, plan.Je, plan.Jump, code: true);
        c.Log.Success($"Telemetry: WBA's send function never sends (patched at 0x{site:X}, its return at 0x{hit.Address + plan.ExitOffset:X}); nothing is sent to WB");
        return true;
    }

    // UFighterGameInstance::RecordEventWithAttributes(const FString& EventName, TArray<FPFGAnalyticsEventAttr>
    // Attributes), void. The Blueprint RecordEventWithAttributes calls it, and so do 14 places in the game's C++.
    // Attributes is passed by value, so the function frees it: every path ends in mov rcx, rbp (Attributes) and
    // a tail jump to the array's destructor. When the analytics object it looks up is null, it takes that path
    // straight away, before building or recording anything; that is the path made unconditional here.

    /// <summary>The start of RecordEventWithAttributes; unique in both builds (Steam, Epic Games Store).</summary>
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

    /// <summary>
    /// A function made to return at once: the pattern is its first bytes, with every call and RIP-relative
    /// displacement left open, since those are what moved between the Steam and Epic Games Store builds
    /// (the code is the same).
    /// </summary>
    /// <param name="Name">For the log.</param>
    /// <param name="Pattern">The function's first bytes.</param>
    /// <param name="EventName">
    /// For the two recorders, whose first bytes are those of three sibling recorders too: the event name the
    /// <c>lea rdx</c> at <see cref="EventNameOffset"/> points at, which is all that tells them apart.
    /// </param>
    internal sealed record EntryReturn(string Name, string Pattern, string? EventName = null);

    /// <summary>Where <c>lea rdx, [rip + disp32]</c> naming the event is, from the start of a recorder.</summary>
    internal const int EventNameOffset = 0x1A;

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
        new("the Store tile's interaction (MvsShopItemCellWidget)", "40 53 48 81 EC E0 00 00 00 48 8D 99 D8 FC FF FF 44 0F B6 C2 48 8B D3 48 8D 4C 24 20 E8 ? ? ?"),
        new("the product page's interaction (MvsShopItemDetailViewWidget)", "40 53 48 81 EC E0 00 00 00 48 8D 99 B8 FB FF FF 44 0F B6 C2 48 8B D3 48 8D 4C 24 20 E8 ? ? ?"),
        new("the purchase dialog's interaction (MvsShopPurchaseModalWidget)", "40 53 48 81 EC E0 00 00 00 48 8D 99 C8 FB FF FF 44 0F B6 C2 48 8B D3 48 8D 4C 24 20 E8 ? ? ?"),
        // IMvsShopAnalyticsGameStore::OnMvsShopAnalyticsEnter/Exit(const FString&...), the interface's own, shared by every screen.
        new("entering a Store screen (game_store_enter)", "48 89 5C 24 10 48 89 6C 24 18 56 57 41 56 48 81 EC D0 00 00 00 48 8B D9 49 8B F1 48 8D 8C 24 F0"),
        new("leaving a Store screen (game_store_exit)", "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 41 56 48 81 EC A0 00 00 00 48 8B 01 49 8B D8 48 8B"),
        // IMvsShopAnalyticsGameStoreUi::OnMvsShopAnalyticsGameStoreUiOpen(), per widget.
        new("the product page opening (MvsShopItemDetailViewWidget)", "40 53 48 81 EC 90 00 00 00 48 8D 99 B0 FB FF FF 48 8B D3 48 8D 4C 24 20 E8 ? ? ? ? 48 8B 03"),
        new("the Store opening (MvsShopWidget)", "48 89 5C 24 08 57 48 81 EC 90 00 00 00 48 8D 99 E8 FA FF FF 48 8B CB E8 ? ? ? ? 48 8B D3 48"),
        // UMvsShopAnalytics::RecordGameStoreUiInteract/Open(const FMvs...Attributes&, UFighterGameInstance*), static.
        // Three more recorders (durable_unlock, wb_iap_event, ...) start with the same bytes; the event name decides.
        new("RecordGameStoreUiInteract (game_store_ui_interact)", RecorderPattern, "game_store_ui_interact"),
        new("RecordGameStoreUiOpen (game_store_ui_open)", RecorderPattern, "game_store_ui_open"),
    ];

    /// <summary>The first 36 bytes of each of UMvsShopAnalytics's static recorders, up to and including the lea rdx that names the event.</summary>
    internal const string RecorderPattern = "48 89 5C 24 08 57 48 83 EC 40 48 8B FA 48 8B D1 48 8D 4C 24 20 E8 ? ? ? ? 48 8D 15 ? ? ? ? 48 8B D8";

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
            var hit = f.EventName == null
                ? c.Patterns.Find("TelemetryShop", f.Pattern)
                : c.Patterns.FindNaming("TelemetryShop", f.Pattern, EventNameOffset, f.EventName);
            if (!hit.Found)
            {
                c.Log.Error($"TelemetryShop: {f.Name} was not found; not patched");
                continue;
            }

            try
            {
                CodeWriter.WriteIf(hit.Address, BytePattern.Parse(f.Pattern), [Ret], code: true);
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
