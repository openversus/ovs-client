#pragma once
#include <string>
//#include "pch/pch.h"
#include "misc/MemoryMgr.h"
#include "Trampoline/Trampoline.h"
#include "Patterns/Patterns.h"
#include "utils/cppython.h"
#include "eSettingsManager/eSettingsManager.h"
#include <chrono>
#include <map>

#define         RVAtoLP( base, offset )     ((PBYTE)base + offset)
#define         FuncMap                     std::map<std::wstring, ULONGLONG>
#define         LibMap                      std::map<std::wstring, FuncMap>
#define         FNAME_STR(FName)            MVSGame::FNameFunc::ToStr(*FName)
typedef         __int64                     int64;

int64           GetGameEntryPoint();
int64           GetUser32EntryPoint();
int64           GetModuleEntryPoint(const wchar_t* name);
int64           GetGameAddr(__int64 addr);
int64           GetUser32Addr(__int64 addr);
int64           GetModuleAddr(__int64 addr, const wchar_t* name);
std::wstring    GetProcessName();
std::wstring    GetProcessNameW();
std::string     GetProcessNameA();
std::wstring    GetDirName();
std::wstring    GetDirName(HMODULE hModule);
std::wstring    GetDirNameW();
std::string     GetDirNameA();
std::wstring    toLower(std::wstring s);
std::string	    toLower(std::string s);
std::wstring    toUpper(std::wstring s);
std::string	    toUpper(std::string s);
std::wstring    GetFileName(std::string filename);
std::wstring    GetFileName(std::wstring filename);
std::wstring    GetFileNameW(std::wstring filename);
std::wstring    GetFileNameW(std::string filename);
std::string     GetFileNameA(std::wstring filename);
std::string     GetFileNameA(std::string filename);
HMODULE         AwaitHModule(const wchar_t* name, uint64_t timeout = 0);
uint64_t        stoui64h(std::string szString);
uint64_t*       FindPattern(void* handle, std::wstring_view bytes);
uint64_t*       FindPattern(std::wstring pattern);
uint64_t*       FindPattern(const wchar_t* pattern);
uint64_t        HookPattern(std::wstring Pattern, const wchar_t* PatternName, void* HookProc, int64_t PatternOffset = 0, PatchTypeEnum PatchType = PatchTypeEnum::PATCH_CALL, uint64_t PrePat = NULL, uint64_t* Entry = nullptr);
uint64_t        GetDestinationFromOpCode(uint64_t Caller, uint64_t Offset = 1, uint64_t FuncLen = 5, uint16_t size = 4);
int32_t         GetOffsetFromOpCode(uint64_t Caller, uint64_t Offset, uint16_t size);
void            ConditionalJumpToJump(uint64_t HookAddress, uint32_t Offset);
void            ConditionalJumpToJump(uint64_t HookAddress);
void            SetCheatPattern(std::wstring pattern, std::wstring name, uint64_t** lpPattern);
LibMap          ParsePEHeader();
int             StringToVK(std::wstring);
void            RaiseException(const wchar_t*, int64_t = 1);
bool            IsHex(char);
bool            IsBase(char c, int = 16);

uint64_t* MakeProxyFromOpCode(Trampoline* GameTramp, uint64_t CallAddr, uint8_t JumpAddrSize, void* ProxyFunctionAddr, PatchTypeEnum PatchType = PATCH_CALL);
template <typename T> void MakeProxyFromOpCode(Trampoline* GameTramp, uint64_t CallAddr, uint8_t JumpAddrSize, void* ProxyFunctionAddr, T** ProxyFuncPtr, PatchTypeEnum PatchType = PATCH_CALL);

class PatternFinder
{
private:
    uint64_t address = 0;

public:
    PatternFinder() = default;
    PatternFinder(const std::wstring pattern) { *this = pattern; }
    operator uint64_t () { return address; }
    operator uint64_t* () { return (uint64_t*)address; }
    operator __int64() { return __int64(address);  }
    operator bool() { return bool(address); }
    PatternFinder& operator+=(const uint64_t b) { address += b; return *this; }
    PatternFinder& operator=(const std::wstring pattern)
    {
        uint64_t returned = CachedPatternsMgr->Load((wchar_t*)pattern.c_str());

        if (returned)
            address = returned;
        else
        {
            address = (uint64_t)FindPattern(pattern);
            CachedPatternsMgr->Save((wchar_t*)pattern.c_str(), address);
        }

        return *this;
    }
    PatternFinder operator+(const int b) {
        PatternFinder obj;
        obj.address = this->address + b;
        return obj;
    }
    PatternFinder operator-(const int b) {
        PatternFinder obj;
        obj.address = this->address - b;
        return obj;
    }
    PatternFinder operator+(const uint64_t b) {
        PatternFinder obj;
        obj.address = this->address + b;
        return obj;
    }
    PatternFinder operator-(const uint64_t b) {
        PatternFinder obj;
        obj.address = this->address - b;
        return obj;
    }
};

struct LibFuncStruct {
    std::wstring FullName;
    std::wstring LibName;
    std::wstring ProcName;
    std::wstring FullNameW;
    std::wstring LibNameW;
    std::wstring ProcNameW;
    bool bIsValid = false;
};

LibFuncStruct	ParseLibFunc(CPPython::string);
void			ParseLibFunc(LibFuncStruct&);
uint64_t*		GetLibProcFromNT(const LibFuncStruct&);
void			PrintErrorProcNT(const LibFuncStruct& LFS, uint8_t bMode);
extern LibMap	IATable;


// Template Definitions

/**
 * Patch a function from its `call` opcode to point to a proxy to the called function
 *
 * @param GameTramp A Trampoline that lives in the game's code space
 * @param CallAddr The address to the `call` opcode
 * @param JumpAddrSize The Size of the Address in the opcode
 * @param ProxyFunctionAddr The address of the Proxy Function
 * @param ProxyFuncPtr The address of a pointer that will reference the game's function
 * @param PatchType CALL or JUMP, defaults to CALL
 *
 */
template <typename T>
void MakeProxyFromOpCode(Trampoline* GameTramp, uint64_t CallAddr, uint8_t JumpAddrSize, void* ProxyFunctionAddr, T** ProxyFuncPtr, PatchTypeEnum PatchType) // Jump size is either 4 or 8
{
    *ProxyFuncPtr = (T*)MakeProxyFromOpCode(GameTramp, CallAddr, JumpAddrSize, ProxyFunctionAddr, PatchType);
}

template <typename T>
void GetProcFromOpCode(uint64_t CallAddr, uint8_t JumpAddrSize, T** ProxyFuncPtr) // Jump size is either 4 or 8
{
    CallAddr = GetGameAddr(CallAddr);
    uint8_t callOpcSize = JumpAddrSize >> 2;
    uint8_t funcLen = callOpcSize + JumpAddrSize;
    uint64_t CalledFuncAddr = GetDestinationFromOpCode(CallAddr, callOpcSize, funcLen, JumpAddrSize);
    *ProxyFuncPtr = (T*)CalledFuncAddr;
}

static void DummyVoidFunc() {}
static void* DummyPtrFunc(...) {return nullptr;}


namespace RegisterHacks {
    void					EnableRegisterHacks();
    typedef void			(__fastcall MoveToRegister)(uint64_t);
    typedef uint64_t		(__fastcall MoveFromRegister)();

    extern bool				bIsEnabled;

    extern MoveToRegister*	MoveToRAX;
    extern MoveToRegister*	MoveToRBX;
    extern MoveToRegister*	MoveToRCX;
    extern MoveToRegister*	MoveToRDX;
    extern MoveToRegister*	MoveToR8;
    extern MoveToRegister*	MoveToR9;
    extern MoveToRegister*	MoveToR10;
    extern MoveToRegister*	MoveToR11;
    extern MoveToRegister*	MoveToR12;
    extern MoveToRegister*	MoveToR13;
    extern MoveToRegister*	MoveToR14;
    extern MoveToRegister*	MoveToR15;

}

uint64_t HashTextSectionOfHost();