using System.Runtime.InteropServices;
using OpenVersus.Hooking;
using OpenVersus.Native;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// The WH_KEYBOARD hook the C++ client installed. It only ever logged F1 presses; kept so the
/// setting means what it meant, installed once (the C++ installed it twice and leaked the first).
/// </summary>
public static unsafe class KeyboardHook
{
    private static nint s_hook;
    private static ILogger? s_log;

    /// <summary>
    /// Installs the hook for the calling thread, with <paramref name="module"/> as the plugin's own
    /// module. False, logged, when Windows refuses it.
    /// </summary>
    public static bool Install(ILogger log, nint module)
    {
        s_log = log;
        s_hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD, (nint)(delegate* unmanaged<int, nint, nint, nint>)&Proc, module, Kernel32.GetCurrentThreadId());
        if (s_hook == 0)
        {
            log.Warn($"Failed To Hook Keyboard FN: 0x{Marshal.GetLastPInvokeError():X}");
            return false;
        }
        return true;
    }

    /// <summary>Removes the hook if it is installed.</summary>
    public static void Remove()
    {
        if (s_hook != 0)
        {
            User32.UnhookWindowsHookEx(s_hook);
            s_hook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint Proc(int code, nint wParam, nint lParam)
    {
        HookGuard.Run("KeyboardProc", (code, lParam), static s =>
        {
            bool released = ((long)s.lParam >> 31 & 1) != 0;
            if (s.code >= 0 && !released && (User32.GetAsyncKeyState(User32.VK_F1) & 0x8000) != 0)
            {
                s_log?.Debug("Attempting F1 action");
            }
        });
        return User32.CallNextHookEx(0, code, wParam, lParam);
    }
}
