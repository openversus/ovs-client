using System.Runtime.InteropServices;
using OpenVersus.Hooking;
using OpenVersus.Native;

namespace OpenVersus;

/// <summary>The .asi's only export. Everything else lives in OpenVersus.Core.</summary>
public static unsafe class Plugin
{
    private static Client? s_client;

    /// <summary>
    /// Called by Ultimate ASI Loader right after LoadLibrary. NativeAOT cannot run managed code
    /// from DllMain, so this export is the only entry point. An exception escaping an
    /// UnmanagedCallersOnly method takes the game down with it, so nothing may leave here.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "InitializeASI")]
    public static void InitializeASI()
    {
        Log? log = null;
        try
        {
            nint self = Kernel32.ModuleFromAddress((nint)(delegate* unmanaged<void>)&InitializeASI);
            string pluginPath = Kernel32.GetModulePath(self);
            string directory = Path.GetDirectoryName(pluginPath)!;
            log = Log.OpenSession(Path.Combine(directory, "logs"), "OpenVersus", fallbackDirectory: directory);
            HookGuard.Attach(log);
            s_client = new Client(log, pluginPath, self);
            s_client.Initialize();
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
}
