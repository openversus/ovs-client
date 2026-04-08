#pragma once
#include <cstdarg>
#include <string>
#include <map>

enum ConsoleColors
{
	BLACK = 0,
	BLUE,
	GREEN,
	AQUA = 3,
    CYAN = 3,
	RED,
	PURPLE,
	YELLOW,
	WHITE,
	GRAY,
	LIGHTBLUE,
	LIGHTGREEN,
	LIGHTAQUA,
	LIGHTRED,
	LIGHTPURPLE,
	LIGHTYELLOW,
	BRIGHTWHITE,
	CLRTRACE = 900,
	CLRDEBUG,
	CLRINFO,
	CLRWARNING,
	CLRERROR,
	CLRCRITICAL,
	CLRSUCCESS,

	GREY = 8, // Synonym
};

extern std::map<ConsoleColors, std::wstring> ColorMap;

void printfColor(ConsoleColors color, std::string Format, ...);
void printfColorNl(ConsoleColors color, std::string Format, ...);
void printfColor(std::string color, std::string Format, ...);
void printfColorNl(std::string color, std::string Format, ...);

void printfColor(ConsoleColors color, std::wstring Format, ...);
void printfColorNl(ConsoleColors color, std::wstring Format, ...);
void printfColor(std::wstring color, std::wstring Format, ...);
void printfColorNl(std::wstring color, std::wstring Format, ...);

void printfColor(ConsoleColors, const wchar_t* const Format, ...);
void printfColorNl(ConsoleColors color, const wchar_t* const Format, ...);
void printfColor(const wchar_t* color, const wchar_t* const Format, ...);
void printfColorNl(const wchar_t* color, const wchar_t* const Format, ...);

void printfColor(ConsoleColors color, const char* const Format, ...);
void printfColorNl(ConsoleColors color, const char* const Format, ...);
void printfColor(const char* color, const char* const Format, ...);
void printfColorNl(const char* color, const char* const Format, ...);

void SetColorW(ConsoleColors color);
void SetColor(ConsoleColors color);
void SetColor(const wchar_t* color);
void SetColor(const char* color);

#ifndef COLORPRINT
#define COLORPRINT
#define printfRed(Format, ...) printfColor(L"\x1b[31m", Format, ## __VA_ARGS__)
#define printfGreen(Format, ...) printfColor(L"\x1b[32m", Format, ## __VA_ARGS__)
#define printfYellow(Format, ...) printfColor(L"\x1b[33m", Format, ## __VA_ARGS__)
#define printfBlue(Format, ...) printfColor(L"\x1b[34m", Format, ## __VA_ARGS__)
#define printfCyan(Format, ...) printfColor(L"\x1b[36m", Format, ## __VA_ARGS__)
//#define printfError(Format, ...) printfColor(L"\x1b[41m", Format, ## __VA_ARGS__)
//#define printfSuccess(Format, ...) printfColor(L"\x1b[42m\x1b[30m", Format, ## __VA_ARGS__)
//#define printfWarning(Format, ...) printfColor(L"\x1b[43m\x1b[30m", Format, ## __VA_ARGS__)
//#define printfInfo(Format, ...) printfColor(L"\x1b[46m\x1b[30m", Format, ## __VA_ARGS__)

#define printfError(Format, ...) printfColorNl(L"\x1b[41m", Format, ## __VA_ARGS__)
#define printfSuccess(Format, ...) printfColorNl(L"\x1b[42m\x1b[30m", Format, ## __VA_ARGS__)
#define printfWarning(Format, ...) printfColorNl(L"\x1b[43m\x1b[30m", Format, ## __VA_ARGS__)
#define printfInfo(Format, ...) printfColorNl(L"\x1b[46m\x1b[30m", Format, ## __VA_ARGS__)

#define SetColorRed() SetColor(L"\x1b[31m")
#define SetColorGreen() SetColor(L"\x1b[32m")
#define SetColorYellow() SetColor(L"\x1b[33m")
#define SetColorBlue() SetColor(L"\x1b[34m")
#define SetColorCyan() SetColor(L"\x1b[36m")
#define SetColorError() SetColor(L"\x1b[41m")
#define SetColorSuccess() SetColor(L"\x1b[42m\x1b[30m")
#define SetColorWarning() SetColor(L"\x1b[43m\x1b[30m")
#define SetColorInfo() SetColor(L"\x1b[46m\x1b[30m")
#define ResetColors() SetColor(L"\033[0m")
#endif
