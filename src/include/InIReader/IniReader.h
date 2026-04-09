#ifndef INIREADER_H
#define INIREADER_H

#include <string>
#include <cstdint>

class CIniReader
{
public:
 CIniReader(const wchar_t* szFileName); 
 size_t ReadInteger(const wchar_t* szSection, const wchar_t* szKey, size_t iDefaultValue);
 float ReadFloat(const wchar_t* szSection, const wchar_t* szKey, float fltDefaultValue);
 bool ReadBoolean(const wchar_t* szSection, const wchar_t* szKey, bool bolDefaultValue);
 wchar_t* ReadString(const wchar_t* szSection, const wchar_t* szKey, const wchar_t* szDefaultValue);
 void WriteInteger(const wchar_t* szSection, const wchar_t* szKey, size_t iValue);
 void WriteFloat(const wchar_t* szSection, const wchar_t* szKey, float fltValue);
 void WriteBoolean(const wchar_t* szSection, const wchar_t* szKey, bool bolValue);
 void WriteString(const wchar_t* szSection, const wchar_t* szKey, const wchar_t* szValue);
 void GetConfigFilename();
private:
  //wchar_t m_szFileName[255];
  std::wstring m_szFileName;
};
#endif//INIREADER_H
