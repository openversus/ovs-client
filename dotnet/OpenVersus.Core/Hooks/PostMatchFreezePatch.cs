using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// Restores the winner's control during the post-match linger. After the match ends locally,
/// the input handler returns early for the local player's slot, which freezes the frame counter
/// the sender uses. Six NOPs over that early return. See HANDOFF-post-match-freeze.
/// </summary>
public static class PostMatchFreezePatch
{
    private static readonly byte[] s_expected = [0x0F, 0x86, 0xFB, 0x01, 0x00, 0x00];
    private static readonly byte[] s_nops = [0x90, 0x90, 0x90, 0x90, 0x90, 0x90];

    /// <summary>
    /// Replaces the early return with NOPs. False when the pattern is missing; throws a
    /// <see cref="PatchException"/> when the bytes there are not the expected jbe.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==PostMatchFreeze==");
        var hit = c.Patterns.Find("PostMatchFreeze");
        if (!hit.Found)
        {
            return false;
        }

        nint site = hit.Address + 0x10;
        CodeWriter.WriteIf(site, s_expected, s_nops, code: true);
        c.Log.Success($"PostMatchFreeze patched at 0x{site:X}");
        return true;
    }
}
