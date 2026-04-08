#include "utils/prettyprint.h"
#include <cstdarg>
#include <cstdio>
#include <string>
#include <Windows.h>

std::map<ConsoleColors, std::wstring> ColorMap = {
	{ BLACK, L"\x1b[30m" },
	{ BLUE, L"\x1b[34m" },
	{ GREEN, L"\x1b[32m" },
	{ AQUA, L"\x1b[36m" },
	{ CYAN, L"\x1b[36m" },
	{ RED, L"\x1b[31m" },
	{ PURPLE, L"\x1b[35m" },
	{ YELLOW, L"\x1b[33m" },
	{ WHITE, L"\x1b[37m" },
	{ GRAY, L"\x1b[90m" },
	{ LIGHTBLUE, L"\x1b[94m" },
	{ LIGHTGREEN, L"\x1b[92m" },
	{ LIGHTAQUA, L"\x1b[96m" },
	{ LIGHTRED, L"\x1b[91m" },
	{ LIGHTPURPLE, L"\x1b[95m" },
	{ LIGHTYELLOW, L"\x1b[93m" },
	{ BRIGHTWHITE, L"\x1b[97m" },
	{ CLRTRACE, L"\x1b[33m" },
	{ CLRDEBUG, L"\x1b[33m" },
	{ CLRINFO, L"\x1b[90m" },
	{ CLRWARNING, L"\x1b[93m" },
	{ CLRERROR, L"\x1b[31m" },
	{ CLRCRITICAL, L"\x1b[41m" },
    { CLRSUCCESS, L"\x1b[42m\x1b[30m" },
};

void SetColorW(ConsoleColors color)
{
	// Convert wide color string to UTF-8
	const std::wstring& wColor = ColorMap[color];
	int utf8Len = WideCharToMultiByte(CP_UTF8, 0, wColor.c_str(), -1, nullptr, 0, nullptr, nullptr);
	if (utf8Len > 0) {
		std::string utf8Color(utf8Len - 1, '\0');
		WideCharToMultiByte(CP_UTF8, 0, wColor.c_str(), -1, &utf8Color[0], utf8Len, nullptr, nullptr);
		printf(utf8Color.c_str());
	}
}

void SetColor(ConsoleColors color)
{
    std::wstring wideColor = ColorMap[color];
    std::string narrowColor;
    narrowColor.reserve(wideColor.size());
    
    for (wchar_t wc : wideColor) {
        narrowColor.push_back(static_cast<char>(wc)); // Safe for ASCII
    }
    
    printf(narrowColor.c_str());
}

void SetColor(const char* color)
{
	printf(color);
}

void SetColor(const wchar_t* color)
{
	wprintf(color);
}

void printfColor(ConsoleColors color, std::string Format, ...)
{
	const char* const formatBuff = Format.c_str();
	printfColor(color, formatBuff);
}

void printfColor(std::string color, std::string Format, ...)
{
    const char* colorBuff = color.c_str();
    const char* const formatBuff = Format.c_str();
    printfColor(colorBuff, formatBuff);
}

void printfColorNl(ConsoleColors color, std::string Format, ...)
{
	const char* const formatBuff = Format.c_str();
	printfColorNl(color, formatBuff);
}

void printfColorNl(std::string color, std::string Format, ...)
{
	const char* colorBuff = color.c_str();
	const char* const formatBuff = Format.c_str();
	printfColorNl(colorBuff, formatBuff);
}

void printfColor(ConsoleColors color, std::wstring Format, ...)
{
	const wchar_t* const formatBuff = Format.c_str();
	printfColor(color, formatBuff);
}

void printfColor(std::wstring color, std::wstring Format, ...)
{
	const wchar_t* colorBuff = color.c_str();
	const wchar_t* const formatBuff = Format.c_str();
	printfColor(colorBuff, formatBuff);
}

void printfColorNl(ConsoleColors color, std::wstring Format, ...)
{
	const wchar_t* const formatBuff = Format.c_str();
	printfColorNl(color, formatBuff);
}

void printfColorNl(std::wstring color, std::wstring Format, ...)
{
	const wchar_t* colorBuff = color.c_str();
	const wchar_t* const formatBuff = Format.c_str();
	printfColorNl(colorBuff, formatBuff);
}

void printfColor(const char* const Format, ...)
{
	va_list args;
	va_start(args, Format);
	vfprintf(stdout, Format, args);
    va_end(args);
}

void printfColor(ConsoleColors color, const char* const Format, ...)
{
	SetColor(color);
	printfColor(Format);
	ResetColors();
}

void printfColor(const char* color, const char* const Format, ...)
{
	SetColor(color);
    printfColor(Format);
	ResetColors();
}

void printfColorNl(ConsoleColors color, const char* const Format, ...)
{
	SetColor(color);
	printfColor(Format);
	ResetColors();
	printf("\n");
}

void printfColorNl(const char* color, const char* const Format, ...)
{
	SetColor(color);
	printfColor(Format);
	ResetColors();
	printf("\n");
}

void printfColor(const wchar_t* const Format, ...)
{
	va_list args;
	va_start(args, Format);
	vwprintf(Format, args);
	va_end(args);
}

void printfColor(ConsoleColors color, const wchar_t* const Format, ...)
{
	va_list args;
	va_start(args, Format);
	SetColor(color);
	vwprintf(Format, args);
	ResetColors();
	va_end(args);
}

void printfColor(const wchar_t* color, const wchar_t* const Format, ...)
{
	va_list args;
	va_start(args, Format);
	SetColor(color);
	vwprintf(Format, args);
	ResetColors();
	va_end(args);
}

void printfColorNl(ConsoleColors color, const wchar_t* const Format, ...)
{
	va_list args;
	va_start(args, Format);
	SetColor(color);
	vwprintf(Format, args);
	ResetColors();
	wprintf(L"\n");
	va_end(args);
}

void printfColorNl(const wchar_t* color, const wchar_t* const Format, ...)
{
	va_list args;
	va_start(args, Format);
	SetColor(color);
	vwprintf(Format, args);
	ResetColors();
	wprintf(L"\n");
	va_end(args);
}
