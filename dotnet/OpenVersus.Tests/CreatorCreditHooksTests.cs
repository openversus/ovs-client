using OpenVersus.Hooks;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// <see cref="CreatorCreditHooks"/>'s patterns against the real bytes, copied from the game's only build:
/// GetFixedRewardTags (0x142289300 to 0x142289318), whose call at +0x13 goes to the fill helper (0x1424de8e0), and
/// the helper's call to TArray&lt;FText&gt;::ResizeGrow (0x1424de935 to 0x1424de961, the call at 0x1424de95c,
/// the helper's +0x7C, to 0x140b7d700). The hook and the append are proven in the game.
/// </summary>
public unsafe class CreatorCreditHooksTests
{
    private const long TagsStart = 0x142289300;
    private const long Fill = 0x1424de8e0;
    private const long GrowStart = 0x1424de935;
    private const long ResizeGrow = 0x140b7d700;

    private static readonly byte[] s_tags = FriendlyFireHooksTests.Hex("40 53 48 83 EC 20 48 8B DA 48 8D 91 30 FE FF FF 48 8B CB E8 C8 55 25 00");
    private static readonly byte[] s_grow = FriendlyFireHooksTests.Hex(
        "0F 84 8C 00 00 00 48 8D 4C 24 30 E8 BB CC 42 00 48 63 5E 08 4C 8B F0 8D 4B 01 89 4E 08 3B 4E 0C 76 0A 8B D3 48 8B CE E8 9F ED 69 FE");

    [Fact]
    public void ThePatternsFindTheFunctionAndTheGrowCall()
    {
        Assert.Equal(0, PatternScanner.FindFirst(s_tags, BytePattern.Parse(CreatorCreditHooks.GetFixedRewardTagsPattern)));
        Assert.Equal(0, PatternScanner.FindFirst(s_grow, BytePattern.Parse(CreatorCreditHooks.FillResizeGrowPattern)));
        Assert.Equal(CreatorCreditHooks.s_getFixedRewardTagsPrologue, s_tags[..CreatorCreditHooks.s_getFixedRewardTagsPrologue.Length]);
    }

    [Fact]
    public void TheFunctionCallsTheFillHelperWhoseGrowCallIsTheOneFound()
    {
        fixed (byte* tags = s_tags)
        fixed (byte* grow = s_grow)
        {
            nint fill = CallSite.Destination((nint)(tags + CreatorCreditHooks.FillCallOffset));
            Assert.Equal(Fill - TagsStart, fill - (nint)tags);

            long growCall = GrowStart + CreatorCreditHooks.FillResizeGrowCall;
            Assert.Equal(Fill + CreatorCreditHooks.FillResizeGrowCallOffset, growCall);
            nint resize = CallSite.Destination((nint)(grow + CreatorCreditHooks.FillResizeGrowCall));
            Assert.Equal(ResizeGrow - GrowStart, resize - (nint)grow);
        }
    }

    [SkippableFact]
    public void EachPatternIsUniqueInTheFinalBuildAndWhereTheDisassemblyFoundIt()
    {
        byte[] mapped = FinalBuild.Mapped();

        Assert.Equal(0x02289300, FinalBuild.Single(mapped, CreatorCreditHooks.GetFixedRewardTagsPattern));
        Assert.Equal(0x024DE95C, FinalBuild.Single(mapped, CreatorCreditHooks.FillResizeGrowPattern) + CreatorCreditHooks.FillResizeGrowCall);
    }
}
