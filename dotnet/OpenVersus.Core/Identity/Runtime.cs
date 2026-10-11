using System.Runtime.InteropServices;
using OpenVersus.Native;

namespace OpenVersus.Identity;

/// <summary>What the game runs on. It decides which identity the client may collect.</summary>
public enum RuntimeEnvironment
{
    /// <summary>Windows itself.</summary>
    NativeWindows,
    /// <summary>Steam's Proton (Linux, Steam Deck).</summary>
    Proton,
    /// <summary>CrossOver (Mac).</summary>
    CrossOver,
    /// <summary>Any other Wine.</summary>
    Wine,
}

/// <summary>Tells the runtimes apart, as the C++ DetectRuntimeEnvironment did.</summary>
public static class Runtime
{
    /// <summary>The runtime this process runs on.</summary>
    public static RuntimeEnvironment Detect()
    {
        nint ntdll = Kernel32.GetModuleHandle("ntdll.dll");
        return Classify(OpenVersus.Wine.IsWine, HasEnv, ntdll != 0 && Kernel32.GetModuleHandle("winemac.drv") != 0, WineBuildId(ntdll));
    }

    /// <summary>
    /// CrossOver first (its bottle variables, the Mac display driver or "crossover" in the Wine
    /// build id), then Proton (Steam's compatibility variables), else plain Wine.
    /// </summary>
    public static RuntimeEnvironment Classify(bool isWine, Func<string, bool> hasEnv, bool macDriverLoaded, string? wineBuildId)
    {
        if (!isWine)
        {
            return RuntimeEnvironment.NativeWindows;
        }

        if (hasEnv("CX_BOTTLE") || hasEnv("CX_ROOT") || macDriverLoaded
            || (wineBuildId?.Contains("crossover", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return RuntimeEnvironment.CrossOver;
        }

        if (hasEnv("STEAM_COMPAT_DATA_PATH") || hasEnv("STEAM_COMPAT_CLIENT_INSTALL_PATH") || hasEnv("PROTON_VERSION"))
        {
            return RuntimeEnvironment.Proton;
        }

        return RuntimeEnvironment.Wine;
    }

    /// <summary>The name the log uses.</summary>
    public static string Name(RuntimeEnvironment runtime) => runtime switch
    {
        RuntimeEnvironment.Proton => "Proton",
        RuntimeEnvironment.CrossOver => "CrossOver",
        RuntimeEnvironment.Wine => "Wine",
        _ => "Native Windows",
    };

    private static bool HasEnv(string name) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

    private static unsafe string? WineBuildId(nint ntdll)
    {
        nint fn = ntdll == 0 ? 0 : Kernel32.GetProcAddress(ntdll, "wine_get_build_id");
        return fn == 0 ? null : Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint>)fn)());
    }
}
