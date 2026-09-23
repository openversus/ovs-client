using OpenVersus.Native;

namespace OpenVersus;

/// <summary>The debug console the C++ client opened when ShowConsole is on, with its banner.</summary>
public static unsafe class ConsoleWindow
{
	private static nint s_handle;

	public static bool IsOpen => s_handle != 0;

	public static bool Create(Log log)
	{
		if (s_handle != 0)
			return true;
		ConsoleApi.FreeConsole();
		if (!ConsoleApi.AllocConsole())
			return false;
		ConsoleApi.SetConsoleCP(ConsoleApi.CP_UTF8);
		ConsoleApi.SetConsoleOutputCP(ConsoleApi.CP_UTF8);
		ConsoleApi.SetConsoleTitle(OvsVersion.ConsoleTitle);
		s_handle = ConsoleApi.GetStdHandle(ConsoleApi.STD_OUTPUT_HANDLE);
		if (ConsoleApi.GetConsoleMode(s_handle, out uint mode))
			ConsoleApi.SetConsoleMode(s_handle, mode | ConsoleApi.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
		log.ConsoleWriter = Write;
		ShowCredits();
		return true;
	}

	public static void Write(string text)
	{
		if (s_handle == 0) return;
		fixed (char* p = text)
			ConsoleApi.WriteConsole(s_handle, p, (uint)text.Length, out _, 0);
	}

	private static void ShowCredits()
	{
		Write("\x1b[31mOpenVersus\x1b[36m - It's better than Parsec\x1b[0m\n");
		Write($"\x1b[33mv{OvsVersion.Current}\x1b[0m\n\n");
		Write("\x1b[36mMaintained by \x1b[38;2;205;46;58mRosettaSt0ned\x1b[36m, \x1b[38;2;236;227;53mTuggernuts\x1b[36m, and the \x1b[38;2;0;255;255mMVS community.\x1b[0m\n\n");
		Write("\x1b[36mBinary releases available at: https://github.com/christopher-conley/OpenVersus\n");
		Write("Source code and binary releases available at: https://github.com/openversus\x1b[0m\n\n");
		Write("\x1b[36mOpenVersus is originally based on the publicly-available code developed by \x1b[38;2;30;117;238mthe\x1b[38;2;214;25;25mthiny\x1b[36m and \x1b[38;2;255;179;25mMultiversusKOTH\x1b[36m, located at: \n\n");
		Write("https://github.com/thethiny/\nhttps://github.com/multiversuskoth/mvs-udp-server\x1b[0m\n\n");
	}
}
