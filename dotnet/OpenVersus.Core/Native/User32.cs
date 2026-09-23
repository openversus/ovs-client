using System.Runtime.InteropServices;

namespace OpenVersus.Native;

public static unsafe partial class User32
{
	public const uint MB_ICONINFORMATION = 0x40;
	public const uint MB_ICONEXCLAMATION = 0x30;
	public const uint MB_ICONERROR = 0x10;
	public const int WH_KEYBOARD = 2;
	public const int VK_F1 = 0x70;

	[LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
	public static partial int MessageBox(nint owner, string text, string caption, uint type);

	[LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
	public static partial nint SetWindowsHookEx(int hookType, nint procedure, nint module, uint threadId);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool UnhookWindowsHookEx(nint hook);

	[LibraryImport("user32.dll")]
	public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

	[LibraryImport("user32.dll")]
	public static partial short GetAsyncKeyState(int key);

	[LibraryImport("user32.dll", EntryPoint = "FindWindowA", StringMarshalling = StringMarshalling.Utf8)]
	public static partial nint FindWindow(string? className, string? windowName);

	[LibraryImport("user32.dll", SetLastError = true)]
	public static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint procedure);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool KillTimer(nint window, nuint id);
}
