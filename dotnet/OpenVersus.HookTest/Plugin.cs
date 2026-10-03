using System.Runtime.InteropServices;
using OpenVersus;
using OpenVersus.Hooking;
using OpenVersus.Memory;
using OpenVersus.Native;
using OpenVersus.P2P;
using Microsoft.Extensions.Logging;

namespace OpenVersus.HookTest;

/// <summary>
/// The Wine harness plugin. It uses the same Core library as OpenVersus.asi to redirect a call
/// and a jmp in wine-host/host.exe into managed code, apply a byte patch, and prove the guard
/// turns an exception into a fallback value. Nothing about the game is in here.
/// </summary>
public static unsafe class Plugin
{
    private static delegate* unmanaged<long, long> s_originalCall;
    private static delegate* unmanaged<long, long> s_originalJump;
    private static delegate* unmanaged<long, long> s_originalGuard;
    private static delegate* unmanaged<long, long> s_originalTable;

    [UnmanagedCallersOnly(EntryPoint = "InitializeASI")]
    public static void InitializeASI()
    {
        Log? log = null;
        try
        {
            string pluginPath = Kernel32.GetModulePath(Kernel32.ModuleFromAddress((nint)(delegate* unmanaged<void>)&InitializeASI));
            log = new Log(Path.ChangeExtension(pluginPath, ".log"));
            HookGuard.Attach(log);
            Log captured = log;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                captured.Info("process-exit hook fired");
                captured.Close();
            };
            Run(log, Path.GetDirectoryName(pluginPath)!);
            log.Flush();
        }
        catch (Exception e)
        {
            try
            {
                log?.Critical($"FATAL: {e}");
            }
            catch { }
        }
    }

    // Each hook is one line: arguments in by value, a static lambda, a fallback out.
    [UnmanagedCallersOnly]
    private static long CallHook(long x) => HookGuard.Run("call", x, static x => s_originalCall(x) + 1, -1L);

    [UnmanagedCallersOnly]
    private static long JumpHook(long x) => HookGuard.Run("jmp", x, static x => s_originalJump(x) + 1000, -1L);

    [UnmanagedCallersOnly]
    private static long TableHook(long x) => HookGuard.Run("table", x, static x => s_originalTable(x) + 100, -1L);

    [UnmanagedCallersOnly]
    private static long GuardHook(long x) => HookGuard.Run("guard", x, static x => throw new InvalidOperationException($"deliberate failure for {x}"), 77L);

    private static void Run(ILogger log, string pluginDirectory)
    {
        byte* image = (byte*)Kernel32.GetModuleHandle(null);
        ReadOnlySpan<byte> bytes = PeImage.ImageInMemory(image);
        log.Info($"host {Kernel32.GetModulePath(0)} at 0x{(nint)image:X}, image size 0x{bytes.Length:X}");

        // The markers are ten-byte "mov r11, imm64" instructions placed in front of each site in
        // host.c, so the pattern finds the site and nothing else. Single '?' wildcards on
        // purpose: that is the C++ client's syntax, and the parser must read it the same way.
        Redirect(log, image, bytes, "49 BB 88 77 66 55 44 33 22 11 E8 ? ? ? ?", (nint)(delegate* unmanaged<long, long>)&CallHook, out s_originalCall);
        Redirect(log, image, bytes, "49 BB 11 22 33 44 55 66 77 88 E9 ? ? ? ?", (nint)(delegate* unmanaged<long, long>)&JumpHook, out s_originalJump);
        Redirect(log, image, bytes, "49 BB AA BB CC DD EE FF 00 11 E8 ? ? ? ?", (nint)(delegate* unmanaged<long, long>)&GuardHook, out s_originalGuard);

        // A byte patch, exact-count style: answer() returns 1234; make it 4321.
        var answer = BytePattern.Parse("B8 D2 04 00 00 C3");
        var hits = PatternScanner.FindAll(bytes, answer);
        if (hits.Count != 1)
        {
            log.Error($"answer pattern matched {hits.Count} times, expected 1");
        }
        else
        {
            nint site = (nint)image + hits[0] + 1;
            try
            {
                CodeWriter.WriteIf(site, [0xD2, 0x04, 0x00, 0x00], [0xE1, 0x10, 0x00, 0x00], code: true);
                log.Info($"answer patched at 0x{site:X}");
            }
            catch (PatchException e)
            {
                log.Error(e.Message);
            }
        }

        SwapTableSlot(log, image, bytes);
        RunNode(log, pluginDirectory);

        // The guarded read must refuse an unmapped address instead of taking the process down.
        bool unmapped = CodeWriter.TryRead(0x10, out long _);
        bool mapped = CodeWriter.TryRead((nint)image, out ushort mz);
        log.Info(!unmapped && mapped && mz == 0x5A4D
            ? "guarded read: ok (unmapped refused, image header read)"
            : $"guarded read: WRONG (unmapped={unmapped}, mapped={mapped}, mz=0x{mz:X})");

        // The page that holds the stubs must be back at its resting protection.
        nint page = Trampoline.Near((nint)image).Base;
        Kernel32.VirtualQuery(page, out MEMORY_BASIC_INFORMATION mbi);
        log.Info($"trampoline page 0x{page:X} protect 0x{mbi.Protect:X} ({(mbi.Protect == Trampoline.RestingProtection ? "as expected" : "WRONG")})");

        log.Info("done");
    }

    /// <summary>
    /// When node binaries sit beside the plugin (run.sh copies the Linux build in when it has been
    /// published), starts the rollback node the way the mod does (under Wine: the Linux build through
    /// start.exe), waits for its port, keeps it alive for a few seconds and stops it. run.sh then checks
    /// that the node process ends once the keepalives stop.
    /// </summary>
    private static void RunNode(ILogger log, string pluginDirectory)
    {
        if (!File.Exists(NodeFiles.LinuxPath(pluginDirectory)) && !File.Exists(NodeFiles.WindowsPath(pluginDirectory)))
        {
            log.Info("node: no binaries beside the plugin; skipped");
            return;
        }

        using var node = new RollbackNode(pluginDirectory, "http://127.0.0.1:1", "127.0.0.1:41235", Wine.IsWine, log, Wine.IsWine ? Wine.UnixPath : null);
        if (!node.Start())
        {
            log.Error("node: WRONG (did not start)");
            return;
        }

        ushort port = node.WaitForPort(TimeSpan.FromSeconds(30));
        log.Info(port == 0 ? "node: WRONG (no port reported in 30 s)" : $"node: port {port} reported");
        // Longer than the node's keepalive timeout (10 s): a node still running at the end of this wait was kept
        // alive by the keepalives, and the exit delay run.sh measures after Stop is the timeout alone.
        Thread.Sleep(TimeSpan.FromSeconds(ParentKeepAlive.TimeoutSeconds + 5));
        node.Stop();
        log.Info("node: stopped");
    }

    /// <summary>
    /// Finds the host's function table in read-only data by the address of thrice() it holds, swaps
    /// that slot for <see cref="TableHook"/>, then tries a second swap that still expects thrice(),
    /// which must be refused and leave the slot as it is.
    /// </summary>
    private static void SwapTableSlot(ILogger log, byte* image, ReadOnlySpan<byte> bytes)
    {
        int at = PatternScanner.FindFirst(bytes, BytePattern.Parse("49 BB 08 07 06 05 04 03 02 01 48 8D 04 49 C3"));
        if (at < 0)
        {
            log.Error("thrice() not found");
            return;
        }

        nint thrice = (nint)image + at;
        nint slot = 0;
        foreach (var s in PeImage.Sections(PeImage.HeadersInMemory(image)))
        {
            if (!s.IsReadOnlyData())
            {
                continue;
            }

            var words = new ReadOnlySpan<nint>(image + s.Rva, (int)(s.VirtualSize / (uint)sizeof(nint)));
            int i = words.IndexOf(thrice);
            if (i >= 0)
            {
                slot = (nint)(image + s.Rva) + i * sizeof(nint);
                break;
            }
        }

        if (slot == 0)
        {
            log.Error($"no read-only slot holds thrice() at 0x{thrice:X}");
            return;
        }

        nint hook = (nint)(delegate* unmanaged<long, long>)&TableHook;
        try
        {
            CodeWriter.SwapPointer(slot, thrice, hook);
            s_originalTable = (delegate* unmanaged<long, long>)thrice;
            log.Info($"table slot 0x{slot:X} swapped from 0x{thrice:X} to 0x{hook:X}");
        }
        catch (PatchException e)
        {
            log.Error($"table slot swap failed: {e.Message}");
            return;
        }

        try
        {
            CodeWriter.SwapPointer(slot, thrice, thrice);
            log.Error("a stale swap was accepted");
        }
        catch (PatchException)
        {
            log.Info(*(nint*)slot == hook ? "stale swap refused, slot unchanged" : "stale swap refused, but the slot CHANGED");
        }
    }

    private static void Redirect(ILogger log, byte* image, ReadOnlySpan<byte> bytes, string pattern, nint hook, out delegate* unmanaged<long, long> original)
    {
        original = null;
        var p = BytePattern.Parse(pattern);
        int at = PatternScanner.FindFirst(bytes, p);
        if (at < 0)
        {
            log.Error($"pattern not found: {pattern}");
            return;
        }
        nint site = (nint)image + at + 10;
        byte[] before = CodeWriter.Read(site, CallSite.Length);
        nint target;
        try
        {
            target = CallSite.Redirect(site, hook);
        }
        catch (PatchException e)
        {
            log.Error($"redirect at 0x{site:X} failed: {e.Message}");
            return;
        }

        original = (delegate* unmanaged<long, long>)target;
        log.Info($"redirected 0x{site:X} ({Log.Hex(before)} -> {Log.Hex(CodeWriter.Read(site, CallSite.Length))}), original target 0x{target:X}");
    }
}
