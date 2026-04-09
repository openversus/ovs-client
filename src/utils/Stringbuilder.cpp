#include "utils/Utils.hpp"
#include <string>
#include <sstream>
#include <cstdarg>

namespace OVS::Utils
{

    StringBuilder::StringBuilder()
    {
        this->classStreamA << "";
        this->classStreamW << L"";
    }

    StringBuilder::StringBuilder(std::string str, ...)
    {
        this->classStreamA << str;
        this->classStreamW << L"";
    }

    StringBuilder::StringBuilder(std::wstring str, ...)
    {
        this->classStreamA << "";
        this->classStreamW << str;
    }

    StringBuilder& StringBuilder::Append(const char* str, ...)
    {
        va_list args;
        va_start(args, str);
        va_list args_copy;
        va_copy(args_copy, args);
        int len = _vscprintf(str, args_copy) + 1;
        va_end(args_copy);
        char* buf = new char[len];
        _vsnprintf_s(buf, len, _TRUNCATE, str, args);
        va_end(args);
        this->classStreamA << buf;
        delete[] buf;
        return *this;
    }

    StringBuilder& StringBuilder::Append(const wchar_t* str, ...)
    {
        va_list args;
        va_start(args, str);
        va_list args_copy;
        va_copy(args_copy, args);
        int len = _vscwprintf(str, args_copy) + 1;
        va_end(args_copy);
        wchar_t* buf = new wchar_t[len];
        _vsnwprintf_s(buf, len, _TRUNCATE, str, args);
        va_end(args);
        this->classStreamW << buf;
        delete[] buf;
        return *this;
    }

    StringBuilder& StringBuilder::Append(const std::string& str, ...)
    {
        this->classStreamA << str;
        return *this;
    }

    StringBuilder& StringBuilder::Append(const std::wstring& str, ...)
    {
        this->classStreamW << str;
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const char* str, ...)
    {
        va_list args;
        va_start(args, str);
        va_list args_copy;
        va_copy(args_copy, args);
        int len = _vscprintf(str, args_copy) + 1;
        va_end(args_copy);
        char* buf = new char[len];
        _vsnprintf_s(buf, len, _TRUNCATE, str, args);
        va_end(args);
        this->classStreamA << buf << '\n';
        delete[] buf;
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const wchar_t* str, ...)
    {
        va_list args;
        va_start(args, str);
        va_list args_copy;
        va_copy(args_copy, args);
        int len = _vscwprintf(str, args_copy) + 1;
        va_end(args_copy);
        wchar_t* buf = new wchar_t[len];
        _vsnwprintf_s(buf, len, _TRUNCATE, str, args);
        va_end(args);
        this->classStreamW << buf << L'\n';
        delete[] buf;
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const std::string& str, ...)
    {
        this->classStreamA << str << '\n';
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const std::wstring& str, ...)
    {
        this->classStreamW << str << L'\n';
        return *this;
    }

    std::wstring StringBuilder::ToString()
    {
        return classStreamW.str();
    }

    std::string StringBuilder::ToStringA()
    {
        return classStreamA.str();
    }
}
