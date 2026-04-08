#pragma once
#include "constants.hpp"
#include "utils/prettyprint.h"
#include "eSettingsManager/eSettingsManager.h"
#include "ovs/OpenVersus.h"
#include "ovs/OVSUtils.h"
#include <cstdarg>
#include <cstdio>
#include <cstdint>
#include <string>
#include <type_traits>
#include <sstream>
#include <map>

namespace OVS::Utils
{
    using namespace std;

    class StringBuilder
    {
    public:
        StringBuilder();
        StringBuilder(std::string str, ...);
        StringBuilder(std::wstring str, ...);
        StringBuilder& Append(const char* str, ...);
        StringBuilder& Append(const wchar_t* str, ...);
        StringBuilder& Append(const std::string& str, ...);
        StringBuilder& Append(const std::wstring& str, ...);
        StringBuilder& AppendLine(const char* str, ...);
        StringBuilder& AppendLine(const wchar_t* str, ...);
        StringBuilder& AppendLine(const std::string& str, ...);
        StringBuilder& AppendLine(const std::wstring& str, ...);
        std::wstring ToString();
        std::string ToStringA();

    private:
        std::stringstream classStreamA;
        std::wstringstream classStreamW;
    };

    struct ParsedURL
    {
        std::wstring Protocol;
        std::wstring Host;
        std::wstring Port;
        std::wstring Path;
        std::map<std::wstring, std::wstring> QueryParams;
        bool bParseSuccess;
        bool bisHTTPS;

        ParsedURL()
        {
            this->Protocol = L"https://";
            this->Host = L"";
            this->Port = L"80";
            this->Path = L"";
            this->QueryParams = {};
            this->bParseSuccess = false;
            this->bisHTTPS = true;
        }
    };

    std::wstring GetTimeStampAsWString();
    std::wstring GetTimeStampAsWString(std::chrono::system_clock::time_point inputTime);
    
    // Core logging function declaration
    // All Write* convenience templates call this eventually
    void WriteOutput(LogLevel loglevel, const std::wstring& message, bool addNL = true);

    // =======================================
    //  NARROW -> WIDE STRING TEMPLATE HELPER
    // =======================================

    template<typename StringType>
    std::wstring ToWideString(const StringType& msg)
    {
        using DecayedType = typename std::decay<StringType>::type;

        // Handle std::wstring
        if constexpr (std::is_same<DecayedType, std::wstring>::value)
        {
            return msg;
        }
        // Handle const wchar_t* and wchar_t[N]
        else if constexpr (std::is_same<DecayedType, const wchar_t*>::value ||
            std::is_same<DecayedType, wchar_t*>::value ||
            std::is_array<DecayedType>::value)
        {
            return std::wstring(msg);
        }
        // Handle std::string
        else if constexpr (std::is_same<DecayedType, std::string>::value)
        {
            return ToWString(msg);
        }
        // Handle const char* and char[N]
        else if constexpr (std::is_same<DecayedType, const char*>::value ||
            std::is_same<DecayedType, char*>::value ||
            std::is_convertible<DecayedType, const char*>::value)
        {
            return ToWString(msg);
        }
        else
        {
            static_assert(std::is_same<DecayedType, std::string>::value ||
                std::is_same<DecayedType, std::wstring>::value,
                "Message must be string, wstring, or string literal");
            return L"";
        }
    }

    // ============================
    //  TEMPLATE LOGGING FUNCTIONS
    // ============================
    template<typename StringType>
    void WriteOutput(LogLevel loglevel, const StringType& message, bool addNL = true)
    {
        WriteOutput(loglevel, ToWideString(message), addNL);
    }

    template<typename StringType>
    void WriteOutput(const StringType& message, bool addNL = true)
    {
        WriteOutput(LOG_INFO, ToWideString(message), addNL);
    }

    template<typename StringType>
    void WriteLog(const StringType& message, bool addNL = true)
    {
        WriteOutput(LOG_DEBUG, ToWideString(message), addNL);
    }

    template<typename StringType>
    void WriteDebug(const StringType& message, bool addNL = true)
    {
        if (SettingsMgr->bDebug)
        {
            WriteOutput(LOG_DEBUG, ToWideString(message), addNL);
        }
    }

    template<typename StringType>
    void WriteInfo(const StringType& message, bool addNL = true)
    {
        WriteOutput(LOG_INFO, ToWideString(message), addNL);
    }

    template<typename StringType>
    void WriteWarn(const StringType& message, bool addNL = true)
    {
        WriteOutput(LOG_WARN, ToWideString(message), addNL);
    }

    template<typename StringType>
    void WriteError(const StringType& message, bool addNL = true)
    {
        WriteOutput(LOG_ERROR, ToWideString(message), addNL);
    }

    template<typename StringType>
    void WriteCritical(const StringType& message, bool addNL = true)
    {
        WriteOutput(LOG_CRITICAL, ToWideString(message), addNL);
    }

    template <typename T>
    void DebugPrintWrapper(T* message, T* color) = delete;

    template <typename T>
    void DebugPrintWrapper(T message, T color) = delete;

    void DebugPrintWrapper(ConsoleColors color = ConsoleColors::CLRDEBUG, const wchar_t* message = L"", ...);
    void DebugPrintWrapper(ConsoleColors color = ConsoleColors::CLRDEBUG, std::wstring message = L"", ...);
    void DebugPrintWrapper(ConsoleColors color = ConsoleColors::CLRDEBUG, const char* message = "", ...);
    void DebugPrintWrapper(ConsoleColors color = ConsoleColors::CLRDEBUG, std::string message = "", ...);
    #ifndef PRINTDEBUGWRAPPER
    #define PrintDebug(Format, ...) DebugPrintWrapper(ConsoleColors::CLRDEBUG, Format, ## __VA_ARGS__)
    #endif // !PRINTDEBUGWRAPPER
    int AutoUpdateHttpRequest(const wchar_t* whost, int port, const wchar_t* wpath, wchar_t* outBuf, int outBufSize);
    bool AutoUpdateJsonGetString(const wchar_t* json, const wchar_t* key, wchar_t* out, int outSize);
    ParsedURL AutoUpdateParseUrl(const std::wstring& url);
    std::wstring ToWideStr(const char* str);
    std::string ToNarrowStr(const wchar_t* str);
    bool SetLocaleConfig();
}
