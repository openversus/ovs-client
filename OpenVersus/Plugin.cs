using System.Runtime.InteropServices;
using OpenVersus.Hooking;
using OpenVersus.Native;

namespace OpenVersus;

/// <summary>The .asi's exports: the loader's entry point and a description of the plugin. Everything else lives in OpenVersus.Core.</summary>
public static unsafe class Plugin
{
    private static Client? s_client;
    private static nint s_info;

    /// <summary>
    /// What this plugin is, for tools that load it on purpose (an installer, a launcher): a UTF-8,
    /// NUL-terminated JSON object with name, version, productVersion (version, commit and build
    /// date) and copyright, the same text as the version resource. The plugin owns the memory,
    /// which lives as long as the plugin does; 0 if it could not be made. The duplicate check does
    /// not use this: it reads the version resource from the file, so nothing is loaded
    /// (<see cref="DuplicatePlugins"/>).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "OpenVersusInfo")]
    public static nint OpenVersusInfo()
    {
        try
        {
            if (s_info == 0)
            {
                string json = $$"""{"name":"{{OvsVersion.Name}}","version":"{{OvsVersion.Current}}","productVersion":"{{OvsVersion.Informational}}","copyright":"{{OvsVersion.Copyright}}"}""";
                nint info = Marshal.StringToCoTaskMemUTF8(json);
                if (Interlocked.CompareExchange(ref s_info, info, 0) != 0)
                {
                    Marshal.FreeCoTaskMem(info);
                }
            }

            return s_info;
        }
        catch
        {
            return 0;
        }
    }

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
