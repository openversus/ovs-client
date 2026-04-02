#include "utils/Utils.hpp"
#include <string>
#include <sstream>

namespace OVS::Utils
{

    StringBuilder::StringBuilder()
    {
        this->classStreamA << "";
        this->classStreamW << L"";
    }

    StringBuilder::StringBuilder(std::string str)
    {
        this->classStreamA << str;
        this->classStreamW << L"";
    }

    StringBuilder::StringBuilder(std::wstring str)
    {
        this->classStreamA << "";
        this->classStreamW << str;
    }

    StringBuilder& StringBuilder::Append(const char* str)
    {
        this->classStreamA << str;
        return *this;
    }

    StringBuilder& StringBuilder::Append(const wchar_t* str)
    {
        this->classStreamW << str;
        return *this;
    }

    StringBuilder& StringBuilder::Append(const std::string& str)
    {
        this->classStreamA << str;
        return *this;
    }

    StringBuilder& StringBuilder::Append(const std::wstring& str)
    {
        this->classStreamW << str;
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const char* str)
    {
        this->classStreamA << str << '\n';
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const wchar_t* str)
    {
        this->classStreamW << str << L'\n';
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const std::string& str)
    {
        this->classStreamA << str << "\n";
        return *this;
    }

    StringBuilder& StringBuilder::AppendLine(const std::wstring& str)
    {
        this->classStreamW << str << L"\n";
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
