using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// Removes the calls to the sunset check altogether. Every direct call to the function becomes
/// "xor eax, eax" plus three NOPs, and the one tail jump becomes "xor eax, eax; ret", which is
/// what a call to the patched function returns anyway. Each site is found by scanning the
/// executable sections for a rel32 call or jmp whose destination is the function's .pdata start,
/// so nothing about the count is hard-coded; the toolkit found 145 calls and one jump in the
/// final build, and the log reports what this run found.
/// </summary>
public static class SunsetCallersPatch
{
    /// <summary>The number of sites the toolkit found in the final build (145 calls, one tail jump). The log compares this run's count with it; nothing depends on the two matching.</summary>
    public const int ExpectedSites = 146;

    /// <summary>
    /// Patches every site found. Needs <see cref="SunsetPatch.FunctionRva"/>, so it runs after
    /// <see cref="SunsetPatch.Apply"/>. True only when every site found was patched.
    /// </summary>
    public static bool Apply(HookContext c)
    {
        c.Log.Info("==Sunset Callers==");
        if (SunsetPatch.FunctionRva == 0)
        {
            c.Log.Error("SunsetCallers: the sunset function is not known (SunsetDate must be enabled and found first)");
            return false;
        }
        nint target = c.Image.Address(SunsetPatch.FunctionRva);
        var sites = new List<(nint Address, bool Jump)>();
        foreach (var section in c.Image.Sections)
        {
            if (!section.IsExecutable())
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = c.Image.Bytes.Slice((int)section.Rva, (int)Math.Min(section.VirtualSize, (uint)(c.Image.Size - section.Rva)));
            for (int i = 0; i + CallSite.Length <= bytes.Length; i++)
            {
                byte op = bytes[i];
                if (op is not (CallSite.CallOpcode or CallSite.JumpOpcode))
                {
                    continue;
                }

                nint at = c.Image.Address(section.Rva + (uint)i);
                if (at + CallSite.Length + BitConverter.ToInt32(bytes[(i + 1)..]) == target)
                {
                    sites.Add((at, op == CallSite.JumpOpcode));
                }
            }
        }
        c.Log.Info($"SunsetCallers: {sites.Count} sites found ({sites.Count(s => s.Jump)} tail jumps); expected {ExpectedSites}");
        if (sites.Count == 0)
        {
            return false;
        }

        int ok = 0;
        foreach (var (at, jump) in sites)
        {
            try
            {
                CodeWriter.Write(at, jump ? [0x31, 0xC0, 0xC3, 0x90, 0x90] : [0x31, 0xC0, 0x90, 0x90, 0x90], code: true);
                ok++;
            }
            catch (PatchException e)
            {
                c.Log.Error($"SunsetCallers: {e.Message}");
            }
        }
        c.Log.Success($"SunsetCallers: {ok}/{sites.Count} sites patched");
        return ok == sites.Count;
    }
}
