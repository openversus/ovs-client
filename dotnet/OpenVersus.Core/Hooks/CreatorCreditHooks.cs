using System.Runtime.InteropServices;
using OpenVersus.Game;
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
    /// <summary>UTauntData's IMvsDisplayedAsReward::GetFixedRewardTags (UEmoteData uses the same one).</summary>
    private const uint GetFixedRewardTagsRva = 0x02289300;
    /// <summary>push rbx; sub rsp, 20h.</summary>
    private static readonly byte[] s_getFixedRewardTagsPrologue = [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20];
    /// <summary>The call into the helper that fills the array, at +0x13.</summary>
    private const int FillCallOffset = 0x13;
    /// <summary>The helper's call to ResizeGrow when the array is full.</summary>
    private const uint FillResizeGrowCallRva = 0x024DE95C;
    private const uint FillRva = 0x024DE8E0;

    /// <summary>UHydraSyncedDataAsset::CustomTags (TArray&lt;FString&gt;).</summary>
    private const int CustomTags = 0x130;
    /// <summary>Where the IMvsDisplayedAsReward sub-object sits in a UTauntData (jmap interface pointer_offset 464).</summary>
    private const int DisplayedAsReward = 0x1D0;
    private static readonly string[] s_creatorPrefixes = ["TS.Dynamic.Creator.", "TS.Fixed.Creator."];

    private static delegate* unmanaged<nint, nint, nint, nint> s_getFixedRewardTags;
    /// <summary>TArray&lt;FText&gt;::ResizeGrow(this, int32 OldNum): makes room for Num, already raised past Max.</summary>
    private static delegate* unmanaged<nint, int, void> s_resizeGrow;
    private static Microsoft.Extensions.Logging.ILogger? s_log;
    /// <summary>Each item's credit, or null for none; items are data assets that live for the session.</summary>
    private static readonly Dictionary<nint, string?> s_credits = [];
    private static bool s_failed;

    /// <summary>
    /// Hooks the implementation. Throws a <see cref="PatchException"/> when this build's code is
    /// not what the disassembly found.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        s_log = c.Log;
        var image = c.Image;
        nint function = image.Address(GetFixedRewardTagsRva);
        CodeWriter.Expect(function, s_getFixedRewardTagsPrologue);
        if (CallSite.Destination(function + FillCallOffset) != image.Address(FillRva))
        {
            throw new PatchException($"GetFixedRewardTags at 0x{function:X} does not call the fill helper at 0x{image.Address(FillRva):X}");
        }

        s_resizeGrow = (delegate* unmanaged<nint, int, void>)CallSite.Destination(image.Address(FillResizeGrowCallRva));
        GameFunctions.FromRva("TArray<FText>::ResizeGrow", (uint)((nint)s_resizeGrow - image.Base), "void TArray<FText>::ResizeGrow(TArray<FText>* this, int32 oldNum)", image);

        nint gateway = EntryHook.Install(function, s_getFixedRewardTagsPrologue, (nint)(delegate* unmanaged<nint, nint, nint, nint>)&GetFixedRewardTags);
        s_getFixedRewardTags = (delegate* unmanaged<nint, nint, nint, nint>)gateway;
        GameFunctions.Register("IMvsDisplayedAsReward::GetFixedRewardTags (UTauntData)", gateway, FunctionSource.Rva,
            $"rva 0x{GetFixedRewardTagsRva:X}, entry hooked; this is the gateway to the original",
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

        try
        {
            if (Credit(self - DisplayedAsReward) is { } creator)
            {
                Append(tags, UE.MakeText($"Creator: {creator}"));
            }
        }
        catch (Exception e)
        {
            // Never again this session: a failure here must not repeat on every page.
            s_failed = true;
            s_log?.Warn($"Creator credits stopped: {e.Message}");
        }

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
                if (!CodeWriter.TryRead(strings.Data + i * sizeof(FString), out FString s) || s.Data == null || s.Count is <= 1 or > 256)
                {
                    continue;
                }

                string tag = s.ToString();
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
    /// as the game's own fill does: Num up by one, ResizeGrow when that passes Max, then the 24 bytes.
    /// The array takes over the reference <paramref name="text"/> holds.
    /// </summary>
    private static void Append(nint array, FText text)
    {
        var header = (TArrayHeader*)array;
        int oldNum = header->Count;
        header->Count = oldNum + 1;
        if (header->Count > header->Max)
        {
            s_resizeGrow(array, oldNum);
        }

        ((FText*)header->Data)[oldNum] = text;
    }
}
