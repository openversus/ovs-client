#pragma once
#include "constants.hpp"
#include "eSettingsManager/eSettingsManager.h"
#include "utils/Utils.hpp"
#include "ovs/OVSUtils.h"
#include "utils/prettyprint.h"
#include <exception>
#include <iostream>
#include <string>
#include <sstream>
#include <cstdlib>
#include <ws2tcpip.h>

using namespace std;
using namespace OVS::Utils;

namespace OVS::Utils
{

    template<typename CBuffer>
    size_t GetSizeOfBuffer(const CBuffer* inputBuffer)
    {
        if (inputBuffer == nullptr)
        {
            return -1;
        }
        size_t BufferSize = 0;
        while (inputBuffer[BufferSize] != static_cast<CBuffer>(0))
        {
            BufferSize++;
            is_null_pointer_v<CBuffer>;
        }
        return BufferSize;
    }

    std::wstring GetTimeStampAsWString()
    {
        return GetTimeStampAsWString(std::chrono::system_clock::now());
    }

    std::wstring GetTimeStampAsWString(std::chrono::system_clock::time_point inputTime)
    {
        time_t tnow = std::chrono::system_clock::to_time_t(inputTime);
        tm localtimeObject;
        //auto localtimeObject = *localtime_s(&tnow);
        localtime_s(&localtimeObject, &tnow);
        wchar_t* tmpBuffer = new wchar_t[40];
        wcsftime(tmpBuffer, 40, L"%Y-%m-%dT%H:%M:%S", &localtimeObject);
        std::wstring timestamp(tmpBuffer);

        size_t bufferSize = GetSizeOfBuffer(tmpBuffer);
        wchar_t* returnObject = new wchar_t[bufferSize + 1];
        wcsncpy_s(returnObject, bufferSize + 1, tmpBuffer, bufferSize);
        std::wstring result = std::wstring(returnObject);

        delete[] tmpBuffer;
        delete[] returnObject;

        return result;
    }

    // Core logging implementation - all Write* templates call this eventually
    void WriteOutput(LogLevel loglevel, const std::wstring& message, bool addNL)
    {
        auto prefixIt = LogPrefixMap.find(loglevel);
        auto streamIt = LogStreams.find(loglevel);
        std::wstring prefix = (prefixIt != LogPrefixMap.end()) ? prefixIt->second : L"";

        //switch (loglevel)
        //{
        //case LOG_INFO:
        //case LOG_WARN:
        //case LOG_CRITICAL:
        //    prefix += L' ';
        //    break;
        //default:
        //    break;
        //}

        std::wostream* stream = (streamIt != LogStreams.end()) ? streamIt->second : &std::wcout;
        std::wstring outputText = prefix + L'[' + (GetTimeStampAsWString()) + L"]: " + message;

        if (addNL)
        {
            outputText += L'\n';
        }

        (*stream) << outputText;
    }

    // Overload for wide string pointers

    void DebugPrintWrapper(ConsoleColors color, const wchar_t* message, ...)
    {
        const wchar_t* safeMessage = message ? message : L"";
        const wchar_t* colorCode = ColorMap[color].c_str();

        va_list args;
        va_start(args, message);
        
        // Calculate required buffer size
        va_list args_copy;
        va_copy(args_copy, args);
        int len = _vscwprintf(safeMessage, args_copy) + 1;
        va_end(args_copy);
        
        wchar_t* formatBuff = new wchar_t[len];
        _vsnwprintf_s(formatBuff, len, _TRUNCATE, safeMessage, args);
        va_end(args);
        const wchar_t* formattedMessage = formatBuff;

        if (SettingsMgr->bDebug)
        {
            printfColor(colorCode, L"%ls", formattedMessage);
        }

        delete[] formatBuff;
    }

    void DebugPrintWrapper(ConsoleColors color, std::wstring message, ...)
    {
        DebugPrintWrapper(color, message.c_str());
    }

    void DebugPrintWrapper(ConsoleColors color, const char* message, ...)
    {
        DebugPrintWrapper(color, ToWideStr(message));
    }
    
    void DebugPrintWrapper(ConsoleColors color, std::string message, ...)
    {
        DebugPrintWrapper(color, ToWideStr(message.c_str()));
    }

    int AutoUpdateHttpRequest(const wchar_t* whost, int port, const wchar_t* wpath,
        wchar_t* outBuf, int outBufSize)
    {
        SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (sock == INVALID_SOCKET) return -1;

        DWORD timeout = 5000;
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));
        setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char*)&timeout, sizeof(timeout));

        // Convert wide strings to narrow strings for socket operations
        char host[256] = {};
        WideCharToMultiByte(CP_UTF8, 0, whost, -1, host, sizeof(host), nullptr, nullptr);

        char path[512] = {};
        WideCharToMultiByte(CP_UTF8, 0, wpath, -1, path, sizeof(path), nullptr, nullptr);

        struct sockaddr_in addr {};
        addr.sin_family = AF_INET;
        addr.sin_port = htons((u_short)port);
        inet_pton(AF_INET, host, &addr.sin_addr);

        if (connect(sock, (struct sockaddr*)&addr, sizeof(addr)) != 0)
        {
            closesocket(sock); return -1;
        }

        // Build HTTP request as narrow string
        char request[1024];
        sprintf_s(request, sizeof(request),
            "GET %s HTTP/1.1\r\nHost: %s:%d\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            path, host, port);

        if (send(sock, request, (int)strlen(request), 0) == SOCKET_ERROR)
        {
            closesocket(sock); return -1;
        }

        int totalRead = 0;
        char* tmpBuf = new char[outBufSize] {};
        while (totalRead < outBufSize - 1)
        {
            int n = recv(sock, tmpBuf + totalRead, outBufSize - 1 - totalRead, 0);
            if (n <= 0) break;
            totalRead += n;
        }
        outBuf[totalRead] = '\0';
        closesocket(sock);

        char* body = strstr(tmpBuf, "\r\n\r\n");
        if (!body) return -1;
        body += 4;
        int bodyLen = totalRead - (int)(body - tmpBuf);
        wchar_t* tmpBufW = new wchar_t[bodyLen + 1];
        size_t convertedChars = 0;
        mbstowcs_s(&convertedChars, tmpBufW, bodyLen + 1, body, bodyLen);
        memmove(outBuf, tmpBufW, convertedChars * sizeof(wchar_t));
        //memmove(outBuf, body, bodyLen + 1);
        return bodyLen;
    }

    bool AutoUpdateJsonGetString(const wchar_t* json, const wchar_t* key, wchar_t* out, int outSize)
    {
        wchar_t pattern[128];
        swprintf_s(pattern, sizeof(key), L"\"%s\"", key);
        const wchar_t* pos = wcsstr(json, pattern);
        if (!pos) return false;
        pos += wcslen(pattern);
        while (*pos == ' ' || *pos == ':' || *pos == '\t') pos++;
        if (*pos != '"') return false;
        pos++;
        int i = 0;
        while (*pos&&* pos != '"' && i < outSize - 1)
            out[i++] = *pos++;
        out[i] = '\0';
        return i > 0;
    }

    ParsedURL AutoUpdateParseUrl(const std::wstring& url)
    {
        const wchar_t* ptr = url.c_str();
        ParsedURL returnObject;

        // 1. Parse protocol
        if (wcsncmp(ptr, L"https://", 8) == 0)
        {
            returnObject.Protocol = L"https://";
            ptr += 8;
        }
        else if (wcsncmp(ptr, L"http://", 7) == 0)
        {
            returnObject.Protocol = L"http://";
            ptr += 7;
        }
        else
        {
            // No protocol specified, assume http
            returnObject.Protocol = L"http://";
        }

        // 2. Find delimiters
        const wchar_t* hostnameStart = ptr;
        const wchar_t* colon = wcschr(ptr, L':');
        const wchar_t* slash = wcschr(ptr, L'/');

        // 3. Parse hostname and port
        if (colon && (!slash || colon < slash))
        {
            // Port is explicitly specified
            returnObject.Host = std::wstring(hostnameStart, colon - hostnameStart);

            // Parse port number
            const wchar_t* portStart = colon + 1;
            const wchar_t* portEnd = slash ? slash : (ptr + wcslen(ptr));
            std::wstring portStr(portStart, portEnd - portStart);

            try
            {
                int PortAsInt = std::stoi(portStr);
                returnObject.Port = portStr;
            }
            catch (...)
            {
                // Fallback to sane defaults if port parsing fails
                if (returnObject.Protocol == L"https://")
                {
                    returnObject.Port = L"443";
                }
                else
                {
                    returnObject.Port = L"80";
                }
            }

            ptr = portEnd;
        }
        else if (slash)
        {
            // No port specified, but path exists
            returnObject.Host = std::wstring(hostnameStart, slash - hostnameStart);
            ptr = slash;
        }
        else
        {
            // No port, no path - just hostname
            returnObject.Host = std::wstring(hostnameStart);
            ptr = hostnameStart + wcslen(hostnameStart);
        }

        // 4. Parse path (everything remaining)
        if (*ptr == L'/')
        {
            returnObject.Path = std::wstring(ptr);
        }
        else
        {
            returnObject.Path = L"/";
        }

        std::wstringstream debugStream;
        debugStream << L"Parsed URL - \n" << L"  Original: " << url.c_str() << L"\n"
            << L"  Protocol: " << returnObject.Protocol << L"\n"
            << L"  Host: " << returnObject.Host << L"\n"
            << L"  Port: " << returnObject.Port << L"\n"
            << L"  Path: " << returnObject.Path << L"\n";

        PrintDebug(debugStream.str());

        returnObject.bParseSuccess = true;
        return returnObject;
    }

    std::wstring ToWideStr(const char* str)
    {
        if (!str || *str == '\0')
            return std::wstring();

        int len = MultiByteToWideChar(CP_UTF8, 0, str, -1, nullptr, 0);
        if (len <= 0)
        {
            return std::wstring();
        }

        std::wstring result(len - 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, str, -1, &result[0], len);
        return result;
    }

    std::string ToNarrowStr(const wchar_t* str)
    {
        if (!str || *str == L'\0')
        {
            return std::string();
        }

        int len = WideCharToMultiByte(CP_UTF8, 0, str, -1, nullptr, 0, nullptr, nullptr);
        if (len <= 0)
            return std::string();

        std::string result(len - 1, '\0');
        WideCharToMultiByte(CP_UTF8, 0, str, -1, &result[0], len, nullptr, nullptr);
        return result;
    }

    bool SetLocaleConfig()
    {
        try
        {
            char* current_locale_cstrI = std::setlocale(LC_ALL, nullptr);
            std::string current_localeI = current_locale_cstrI ? current_locale_cstrI : "C";

            wchar_t localeName[LOCALE_NAME_MAX_LENGTH];
            int result = GetUserDefaultLocaleName(localeName, LOCALE_NAME_MAX_LENGTH);

            if (result == 0)
            {
                // Failed to get user locale, fallback to "en-US"
                wcscpy_s(localeName, L"en-US");
            }

            // Create the UTF-8 variant of the locale (e.g., "en-US.UTF-8")
            std::wstring utf8_locale = std::wstring(localeName) + L".UTF-8";

            // Set the UTF-8 variant of their actual locale and configure console for UTF-8
            // for cross-platform compatibility (Windows, Proton, Steam Deck)
            //_wsetlocale(LC_ALL, utf8_locale.c_str());
            SetConsoleCP(CP_UTF8);
            SetConsoleOutputCP(CP_UTF8);

            //auto currentLocale = setlocale(LC_ALL, nullptr);
            std::locale::global(std::locale("en_US.UTF-8"));
            std::wclog.imbue(std::locale());
            std::wcin.imbue(std::locale());
            std::wcout.imbue(std::locale());
            std::wcerr.imbue(std::locale());
            std::clog.imbue(std::locale());
            std::cin.imbue(std::locale());
            std::cout.imbue(std::locale());
            std::cerr.imbue(std::locale());

            _free_locale(nullptr);


            HANDLE conHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            DWORD conMode = 0;
            GetConsoleMode(conHandle, &conMode);
            conMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(conHandle, conMode);

            return true;
        }
        catch (const std::exception& e)
        {
            printfError(L"Failed to set locale: " + ToWideStr(e.what()));
            return false;
        }
    }
}
