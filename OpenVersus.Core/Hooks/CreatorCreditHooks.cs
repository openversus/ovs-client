using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Hooking;
using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// "Creator: &lt;Name&gt;" among the tags of an emote's or taunt's page, for OVS cosmetics made by
/// the community. A skin carries the credit as a TS.Fixed.Creator tag in its own TagSystemTags; an
/// emote or taunt has no tag list (its page shows the labels of its universes), so the credit sits
/// in its CustomTags as "TS.Dynamic.Creator.&lt;Name&gt;" or "TS.Fixed.Creator.&lt;Name&gt;" and is
/// added here to what the game returns.
/// <para>
/// The page calls IMvsDisplayedAsReward::GetFixedRewardTags on the item directly (the Blueprint
/// thunk execGetFixedRewardTags saw no call, 2026-10-02); UTauntData and UEmoteData share one
/// implementation, slot 0x40 of their interface vtables. It fills the array through a helper that
/// grows it with TArray&lt;FText&gt;::ResizeGrow; the credit is appended the same way.
/// </para>
/// </summary>
public static unsafe class CreatorCreditHooks
{
    // The patterns live here, not in OpenVersus.toml: credits are not something a player switches off.
    // Whole instructions from the game's only build, rel32 targets as wildcards; each matches once there
    // (CreatorCreditHooksTests has the bytes).
    /// <summary>UTauntData's IMvsDisplayedAsReward::GetFixedRewardTags (UEmoteData uses the same one; RVA 0x02289300).</summary>
    internal const string GetFixedRewardTagsPattern = "40 53 48 83 EC 20 48 8B DA 48 8D 91 30 FE FF FF";
    /// <summary>The fill helper's call to ResizeGrow when the array is full (0x024DE935, the call at 0x024DE95C).</summary>
    internal const string FillResizeGrowPattern = "0F 84 ? ? ? ? 48 8D 4C 24 30 E8 ? ? ? ? 48 63 5E 08 4C 8B F0 8D 4B 01 89 4E 08 3B 4E 0C 76 0A 8B D3 48 8B CE E8 ? ? ? ?";
    /// <summary>Where the call to ResizeGrow is, from the start of <see cref="FillResizeGrowPattern"/>.</summary>
    internal const int FillResizeGrowCall = 39;
    /// <summary>push rbx; sub rsp, 20h: the instructions the entry hook moves.</summary>
    internal static readonly byte[] s_getFixedRewardTagsPrologue = [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20];
    /// <summary>The call into the helper that fills the array, from the start of GetFixedRewardTags.</summary>
    internal const int FillCallOffset = 0x13;
    /// <summary>The ResizeGrow call, from the start of the fill helper.</summary>
    internal const int FillResizeGrowCallOffset = 0x7C;

    /// <summary>UHydraSyncedDataAsset::CustomTags (TArray&lt;FString&gt;).</summary>
    private const int CustomTags = 0x130;
    /// <summary>Where the IMvsDisplayedAsReward sub-object sits in a UTauntData (jmap interface pointer_offset 464).</summary>
    private const int DisplayedAsReward = 0x1D0;
    private static readonly int s_countOffset = (int)Marshal.OffsetOf<TArrayHeader>(nameof(TArrayHeader.Count));
    private static readonly string[] s_creatorPrefixes = ["TS.Dynamic.Creator.", "TS.Fixed.Creator."];

    private static delegate* unmanaged<nint, nint, nint, nint> s_getFixedRewardTags;
    /// <summary>TArray&lt;FText&gt;::ResizeGrow(this, int32 OldNum): makes room for Num, already raised past Max.</summary>
    private static delegate* unmanaged<nint, int, void> s_resizeGrow;
    /// <summary>Each item's credit, or null for none; items are data assets that live for the session.</summary>
    private static readonly Dictionary<nint, string?> s_credits = [];
    private static bool s_failed;

    /// <summary>
    /// Hooks the implementation. False when a pattern is missing; throws a <see cref="PatchException"/>
    /// when this build's code is not what the disassembly found.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==Creator Credits==");
        var image = c.Image;
        var tags = c.Patterns.Find("CreatorCredits", GetFixedRewardTagsPattern);
        var grow = c.Patterns.Find("CreatorCreditsResizeGrow", FillResizeGrowPattern);
        if (!tags.Found || !grow.Found)
        {
            c.Log.Error("Creator credits: GetFixedRewardTags was not found; emotes and taunts will not show their creator");
            return false;
        }

        nint fill = CallSite.Destination(tags.Address + FillCallOffset);
        nint growCall = grow.Address + FillResizeGrowCall;
        if (growCall != fill + FillResizeGrowCallOffset)
        {
            throw new PatchException($"GetFixedRewardTags at 0x{tags.Address:X} calls a fill helper at 0x{fill:X}, which does not grow the array at 0x{growCall:X}");
        }

        s_resizeGrow = (delegate* unmanaged<nint, int, void>)CallSite.Destination(growCall);
        GameFunctions.Register("TArray<FText>::ResizeGrow", (nint)s_resizeGrow, FunctionSource.CallSite, $"call at 0x{growCall:X}", "void TArray<FText>::ResizeGrow(TArray<FText>* this, int32 oldNum)", image);

        nint gateway = EntryHook.Install(tags.Address, s_getFixedRewardTagsPrologue, (nint)(delegate* unmanaged<nint, nint, nint, nint>)&GetFixedRewardTags);
        s_getFixedRewardTags = (delegate* unmanaged<nint, nint, nint, nint>)gateway;
        GameFunctions.Register("IMvsDisplayedAsReward::GetFixedRewardTags (UTauntData)", gateway, FunctionSource.Pattern,
            $"{tags.Name} at 0x{tags.Address:X}, entry hooked; this is the gateway to the original",
            "TArray<FText>* GetFixedRewardTags(IMvsDisplayedAsReward* this, TArray<FText>* result, UObject* worldContext)", image);
        c.Log.Success("Creator credits hooked: OVS emotes and taunts show their creator");
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint GetFixedRewardTags(nint self, nint result, nint worldContext)
    {
        nint tags = s_getFixedRewardTags(self, result, worldContext);
        if (s_failed || !Engine.IsUp || !UE.Ready)
        {
            return tags;
        }

        // Never again this session after a failure: it must not repeat on every page.
        s_failed = !HookGuard.Run("CreatorCredits", (self, tags), static s =>
        {
            if (Credit(s.self - DisplayedAsReward) is { } creator)
            {
                Append(s.tags, UE.MakeText($"Creator: {creator}"));
            }

            return true;
        }, false);
        return tags;
    }

    private static string? Credit(nint item)
    {
        if (s_credits.TryGetValue(item, out string? cached))
        {
            return cached;
        }

        string? creator = null;
        if (CodeWriter.TryRead(item + CustomTags, out TArrayHeader strings) && strings.Data != 0 && strings.Count is > 0 and <= 32)
        {
            for (int i = 0; i < strings.Count && creator == null; i++)
            {
                if (!GameStrings.TryReadFString(ProcessMemory.Instance, strings.Data + i * sizeof(FString), out string tag))
                {
                    continue;
                }

                foreach (string prefix in s_creatorPrefixes)
                {
                    if (tag.StartsWith(prefix, StringComparison.Ordinal) && tag.Length > prefix.Length)
                    {
                        creator = tag[prefix.Length..];
                    }
                }
            }
        }

        s_credits[item] = creator;
        return creator;
    }

    /// <summary>
    /// Adds <paramref name="text"/> at the end of the TArray&lt;FText&gt; at <paramref name="array"/>,
    /// as the game's own fill does: when it is full, Num up by one and ResizeGrow; then the 24 bytes,
    /// and Num up by one when it was not full. The array takes over the reference <paramref name="text"/>
    /// holds. Every read and write is guarded; one that fails throws, leaving Num as it was.
    /// </summary>
    private static void Append(nint array, FText text)
    {
        if (!CodeWriter.TryRead(array, out TArrayHeader header))
        {
            throw new InvalidOperationException($"the tag array at 0x{array:X} could not be read");
        }

        int oldNum = header.Count;
        bool grow = oldNum + 1 > header.Max;
        if (grow)
        {
            if (!CodeWriter.TryWrite(array + s_countOffset, oldNum + 1))
            {
                throw new InvalidOperationException($"the tag array at 0x{array:X} could not be grown");
            }

            s_resizeGrow(array, oldNum);
            if (!CodeWriter.TryRead(array, out header) || header.Data == 0)
            {
                throw new InvalidOperationException($"the tag array at 0x{array:X} could not be read after growing");
            }
        }

        if (!CodeWriter.TryWrite(header.Data + oldNum * sizeof(FText), text))
        {
            if (grow)
            {
                CodeWriter.TryWrite(array + s_countOffset, oldNum);
            }

            throw new InvalidOperationException($"the credit could not be written into the tag array at 0x{header.Data:X}");
        }

        if (!grow && !CodeWriter.TryWrite(array + s_countOffset, oldNum + 1))
        {
            throw new InvalidOperationException($"the tag array at 0x{array:X} could not take the credit");
        }
    }
}
