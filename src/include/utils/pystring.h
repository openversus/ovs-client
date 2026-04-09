#pragma once

#include <string>
#include <iostream>
#include <vector>

template <class T, class U>
int ifin(T LHS, U RHS, size_t SIZE)
{
    int found = -1;
    for (int i = 0; i < SIZE; i++)
    {
        if (LHS == RHS[i])
        {
            found = i;
            break;
        }
    }
    return found;
}

#define is_in(LHS, RHS, SIZE) (ifin(LHS, RHS, SIZE) != -1)

class PyString : public std::wstring
{
private:
    std::wstring _char_to_charptr(wchar_t c)
    {
        wchar_t C[1] = {c};
        return std::wstring(C);
    }

public:
    // Constructors
    PyString() : std::wstring(){};
    PyString(const PyString &s) : std::wstring(s){};
    PyString(const PyString *s) : std::wstring(*s){};
    PyString(const std::wstring &s) : std::wstring(s){};
    PyString(const std::wstring *s) : std::wstring(*s){};
    PyString(const wchar_t *c) : std::wstring(c){};
    PyString(wchar_t *c) : std::wstring(c){};
    PyString(wchar_t c) : std::wstring(_char_to_charptr(c)){};

    // Python functions
    std::vector<PyString> split(PyString ToFind = L" ", int MaxSplit = -1); // Need a split function that is "OR" instead of "AND".
    std::vector<PyString> rsplit(PyString ToFind = L" ", int MaxSplit = -1);
    PyString strip(PyString ToStrip = L"\n\t\r ");
    PyString lower();
    PyString upper();
    PyString join(PyString *, uint64_t size);
    PyString join(std::vector<PyString> vec) { return this->join(&vec[0], vec.size()); }
    PyString replace(PyString Replacement, PyString ReplaceWith);
    wchar_t index(int64_t i) { return i >= 0 ? std::wstring::operator[](i) : std::wstring::operator[](this->length() - 1); }
    bool startswith(PyString Start);
    bool endswith(PyString End);

    // Convertors
    operator std::wstring() { return std::wstring(*this); }
    operator const wchar_t *() { return this->c_str(); }
    operator wchar_t *() { return (wchar_t *)this->c_str(); }

    // Operators
    template <class T>
    wchar_t operator[](T i) { return index(i); }
};
