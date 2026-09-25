using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// Lets .pak and .utoc files with bad signatures load. The C++ client redirected the tail jump
/// at pattern+0x37 to an empty function; a "ret" in place of that jump has the same effect and
/// no managed code runs on the pak-loading path at all.
/// </summary>
public static class SigCheckPatch
{
    /// <summary>
    /// Turns the tail jump into a ret. False when the pattern is missing; throws a
    /// <see cref="PatchException"/> when the byte there is not a jmp.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==DisableSignatureCheck==");
        var hit = c.Patterns.Find("SigCheck");
        if (!hit.Found)
        {
            return false;
        }

        nint site = hit.Address + 0x30 + 7;
        CodeWriter.WriteIf(site, [CallSite.JumpOpcode], [0xC3], code: true);
        c.Log.Success($"SigCheck patched: jmp at 0x{site:X} is now ret");
        return true;
    }
}
