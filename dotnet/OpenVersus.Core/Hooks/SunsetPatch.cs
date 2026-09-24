using System.Runtime.InteropServices;
using OpenVersus.Game;
using OpenVersus.Memory;

namespace OpenVersus.Hooks;

/// <summary>
/// Bypasses the game's death date with bytes alone. The date check is a function the game
/// calls thousands of times a minute (measured 2026-09-23), so nothing managed may sit in it.
/// The C++ client redirected two sites in it to a checker that returned false; these two
/// patches make the function return false on both paths itself:
///
/// - On the first-call path, after the thread-safe-init header claims the init, skip building
///   the sunset date and go straight to the footer that releases the init, then to the
///   comparison path (a two-byte jump at pattern+0x15). Skipping the footer would deadlock
///   any other thread entering the function, so it stays.
/// - On the comparison path, replace the load that precedes the comparison with
///   "xor eax, eax; jmp epilogue" (at pattern-0x2F), so the function returns false.
///
/// The functions and data the C++ recorded from this site are still registered, for the log.
/// </summary>
public static unsafe class SunsetPatch
{
    private static long s_calls;

    /// <summary>How many times the game has called the sunset check, when counting is on.</summary>
    public static long Calls => Interlocked.Read(ref s_calls);
    public static bool Counting { get; private set; }
    /// <summary>The function's start RVA, from .pdata, once Apply has run.</summary>
    public static uint FunctionRva { get; private set; }

    public const string InitThreadHeaderName = "Init_thread_header";
    public const string InitThreadFooterName = "Init_thread_footer";
    public const string FDateTimeName = "FDateTime::FDateTime";
    public const string SunsetDateName = "kSunsetDate";

    public static bool Apply(HookContext c, bool count = false)
    {
        c.Log.Info("==Override Sunset Function==");
        var hit = c.Patterns.Find("SunsetDate", c.Settings.Pattern("pSunsetDate"));
        if (!hit.Found)
        {
            return false;
        }

        nint p = hit.Address;
        var function = PeImage.FunctionContaining(c.Image.Bytes, c.Image.Rva(p));
        if (function is { } f)
        {
            FunctionRva = f.Begin;
            c.Log.Debug($"SunsetDate: function 0x{c.Image.Address(f.Begin):X}-0x{c.Image.Address(f.End):X} from .pdata");
        }
        else
        {
            c.Log.Warn("SunsetDate: no .pdata entry covers the pattern; SunsetCallers cannot be applied");
        }

        GameFunctions.FromCallSite(InitThreadHeaderName, p + 7, "call at SunsetDate+7", "void Init_thread_header(int* tss)", c.Image);
        GameFunctions.FromCallSite(InitThreadFooterName, p + 0x47, "call at SunsetDate+47", "int Init_thread_footer(int* tss)", c.Image);
        GameFunctions.FromCallSite(FDateTimeName, p + 0x3B, "call at SunsetDate+3B", "void FDateTime::FDateTime(uint64_t* this, int y, int m, int d, int h, int min, int s, int ms)", c.Image);
        GameFunctions.Register(SunsetDateName, CallSite.Destination(p + 0x19, displacementOffset: 3, instructionLength: 7), FunctionSource.CallSite, "lea at SunsetDate+19", "uint64_t kSunsetDate (data)", c.Image);

        // pattern+0x15: "mov [rsp+38h], edi" begins the date construction; jump over it to
        // the "lea rcx, guard; call Init_thread_footer" at pattern+0x40.
        string? error = CodeWriter.WriteIf(p + 0x15, [0x89, 0x7C, 0x24, 0x38], [0xEB, 0x29], code: true);
        if (error != null)
        {
            c.Log.Error($"SunsetDate: {error}");
            return false;
        }

        // pattern-0x2F: "lea rcx, [rsp+50h]" (5 bytes) becomes "xor eax, eax; jmp +0x13" to
        // the epilogue at pattern-0x18, returning false. The fifth byte is never reached.
        // With counting on, it becomes "call counter; jmp +0x10" instead (the C++ client's
        // shape), and the counter returns the false.
        nint site = p - 0x2F;
        ReadOnlySpan<byte> lea = [0x48, 0x8D, 0x4C, 0x24, 0x50];
        if (!lea.SequenceEqual(CodeWriter.Read(site, 5)))
        {
            c.Log.Error($"SunsetDate: expected lea at 0x{site:X}, found {Log.Hex(CodeWriter.Read(site, 5))}; not patching");
            return false;
        }
        if (count)
        {
            error = CallSite.Inject(site, (nint)(delegate* unmanaged<nint, byte>)&Count, jump: false, out _);
            if (error == null)
            {
                error = CodeWriter.Write(site + 5, [0xEB, 0x10], code: true);
            }

            Counting = error == null;
        }
        else
        {
            error = CodeWriter.Write(site, [0x31, 0xC0, 0xEB, 0x13, 0x90], code: true);
        }

        if (error != null)
        {
            c.Log.Error($"SunsetDate: {error}");
            return false;
        }

        foreach (string name in new[] { InitThreadHeaderName, InitThreadFooterName, FDateTimeName, SunsetDateName })
        {
            c.Log.Debug(GameFunctions.Find(name)!.ToString());
        }

        c.Log.Success($"Sunset Function patched at 0x{site:X} and 0x{p + 0x15:X}{(count ? " (counting calls)" : "")}");
        return true;
    }

    /// <summary>Nothing here can throw or allocate, so no guard: one increment and return false.</summary>
    [UnmanagedCallersOnly]
    private static byte Count(nint unused)
    {
        Interlocked.Increment(ref s_calls);
        return 0;
    }
}
