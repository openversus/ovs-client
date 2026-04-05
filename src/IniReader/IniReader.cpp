#include "includes.h"
#include "constants.hpp"
#include "utils/prettyprint.h"
#include "IniReader/IniReader.h"
#include <iostream>
#include "utils/Utils.hpp"
#include "ovs/OVSUtils.h"
#include <string>
#include <vector>
#include <windows.h>
#include <cinttypes>
using namespace std;
#pragma warning(disable:4996)
EXTERN_C IMAGE_DOS_HEADER __ImageBase;

CIniReader::CIniReader(const wchar_t* szFileName)
{
    std::wstring dirName = GetDirName((HMODULE)&__ImageBase);
    std::wstring iniName = OVS::OVS_Config_File;

    if (wcscmp(szFileName, L"") == 0) //if (szFileName == L"")
    {
        m_szFileName = dirName + L"\\" + iniName;

    }
    else
    {
        m_szFileName = dirName + L"\\" + szFileName;
    }
}

void CIniReader::GetConfigFilename()
{
    printfYellow(L"[OVS] INI File: %s\n", m_szFileName.c_str());
}

size_t CIniReader::ReadInteger(const wchar_t* szSection, const wchar_t* szKey, size_t iDefaultValue)
{
    size_t iResult = GetPrivateProfileInt(szSection, szKey, static_cast<int>(iDefaultValue), m_szFileName.c_str());
    return iResult;
}

float CIniReader::ReadFloat(const wchar_t* szSection, const wchar_t* szKey, float fltDefaultValue)
{
 wchar_t szResult[255];
 wchar_t szDefault[255];
 float fltResult;
 _snwprintf_s(szDefault, 255, _TRUNCATE, L"%f",fltDefaultValue);
 GetPrivateProfileString(szSection,  szKey, szDefault, szResult, 255, m_szFileName.c_str()); 
 fltResult = (float)_wtof(szResult);
 return fltResult;
}

bool CIniReader::ReadBoolean(const wchar_t* szSection, const wchar_t* szKey, bool bolDefaultValue)
{
 wchar_t szResult[255];
 wchar_t szDefault[255];
 bool bolResult;
 _snwprintf_s(szDefault, 255, _TRUNCATE, L"%s", bolDefaultValue ? L"true" : L"false");
 GetPrivateProfileString(szSection, szKey, szDefault, szResult, 255, m_szFileName.c_str()); 
 bolResult =  (wcscmp(szResult, L"true") == 0 || 
        wcscmp(szResult, L"True") == 0) ? true : false;
 return bolResult;
}

wchar_t* CIniReader::ReadString(const wchar_t* szSection, const wchar_t* szKey, const wchar_t* szDefaultValue)
{
 wchar_t* szResult = new wchar_t[255];
 wmemset(szResult, 0x00, 255);
 GetPrivateProfileString(szSection,  szKey, 
        szDefaultValue, szResult, 255, m_szFileName.c_str()); 
 return szResult;
}

void CIniReader::WriteInteger(const wchar_t* szSection, const wchar_t* szKey, size_t iValue)
{
    wchar_t szValue[255];
    _snwprintf_s(szValue, 255, _TRUNCATE, L"%" PRIu64, iValue);
    WritePrivateProfileString(szSection, szKey, szValue, m_szFileName.c_str());
}

void CIniReader::WriteFloat(const wchar_t* szSection, const wchar_t* szKey, float fltValue)
{
    wchar_t szValue[255];
    _snwprintf_s(szValue, 255, _TRUNCATE, L"%f", fltValue);
    WritePrivateProfileString(szSection, szKey, szValue, m_szFileName.c_str());
}

void CIniReader::WriteBoolean(const wchar_t* szSection, const wchar_t* szKey, bool bolValue)
{
    wchar_t szValue[255];
    _snwprintf_s(szValue, 255, _TRUNCATE, L"%s", bolValue ? L"true" : L"false");
    WritePrivateProfileString(szSection, szKey, szValue, m_szFileName.c_str());
}

void CIniReader::WriteString(const wchar_t* szSection, const wchar_t* szKey, const wchar_t* szValue)
{
    WritePrivateProfileString(szSection, szKey, szValue, m_szFileName.c_str());
}
