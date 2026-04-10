// =============================================================================
// NotificationPoller.cpp
//
// Background thread that polls the OpenVersus live server's /ovs/notifications
// endpoint every 2 seconds and dispatches queued notifications to the right
// handlers.
//
// Currently handles:
//
//   match_cancel    — server signals that the opponent left during pregame.
//                     Walks the live game state machine, identifies the
//                     active pre-match state, and invokes the matching
//                     UE failure handler via ProcessEvent. The player is
//                     gracefully returned to the lobby just like the native
//                     2-minute timeout would do, but instantly.
//
//   admin_banner    — server pushes a banner notification to all (or one)
//                     connected clients. Pops up the in-game native widget
//                     with two lines of text. Used for admin announcements
//                     like "server restarting in 5 minutes".
//
// JSON format expected from /ovs/notifications:
//
//   [
//     { "type": "match_cancel",  "title": "...",     "message": "...",
//       "timestamp": 1775778608049 },
//
//     { "type": "admin_banner",  "title": "Top line",
//       "message": "Bottom line", "timeout": 10.0,
//       "timestamp": 1775778608049 },
//
//     ...
//   ]
//
// Both fields title and message are optional but at least one is recommended.
//
// =============================================================================
#include "ovs/NotificationPoller.h"
#include "ovs/OpenVersus.h"
#include "mvs/mvs.h"
#include "Utils/Utils.hpp"
#include <atomic>
#include <thread>
#include <string>
#include <cstring>
#include <cstdio>
#include <windows.h>
#include <winhttp.h>

#pragma comment(lib, "winhttp.lib")

namespace OVS::NotificationPoller {

    // ─────────────────────────────────────────────────────────────────────────
    // Module state
    // ─────────────────────────────────────────────────────────────────────────
    static std::atomic<bool> g_Running{ false };
    static std::thread       g_Thread;
    static std::string       g_ServerHost;   // hostname only (no scheme)
    static int               g_ServerPort = 8000;

    // ─────────────────────────────────────────────────────────────────────────
    // ProcessEvent function pointer (UE reflection dispatcher)
    // RVA from Dumper-7 OffsetsInfo.json: OFFSET_PROCESSEVENT = 0x02D3D810
    // ─────────────────────────────────────────────────────────────────────────
    constexpr uint64_t RVA_PROCESS_EVENT = 0x02D3D810;
    typedef void (__fastcall* ProcessEventFn)(void* thisObj, void* ufunction, void* params);

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveFName — converts an FName.Index integer back to its string form
    // using the game's UE FName::ToString function (already resolved by
    // HookUEFuncs at startup as MVSGame::FName::ToStringPtr).
    // Returns true on success and writes a null-terminated ASCII string to
    // outBuf. Safe to call inside __try blocks.
    // ─────────────────────────────────────────────────────────────────────────
    static bool ResolveFName(int32_t fnIdx, char* outBuf, int outBufSize)
    {
        if (!UE::FName::ToStringPtr || outBufSize < 2 || !outBuf) return false;
        outBuf[0] = '\0';
        __try {
            // FString output is { wchar_t* Data; int32 Count; int32 Max; } = 16 bytes
            uint8_t rawStr[24] = {};
            UE::FName tn; tn.Index = fnIdx; tn.Number = 0;
            UE::FName::ToStringPtr(&tn, (UE::FString*)rawStr);
            wchar_t* sd = *(wchar_t**)rawStr;
            int32_t  sc = *(int32_t*)(rawStr + 8);
            if (sd && sc > 0 && sc < 256) {
                for (int c = 0; c < sc - 1 && c < outBufSize - 1; c++)
                    outBuf[c] = (char)sd[c];
                return true;
            }
        } __except(EXCEPTION_EXECUTE_HANDLER) {}
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FindUClassByName — heap scans for a UObject whose FName matches the
    // given class name. Used to locate UClass objects (which are themselves
    // UObjects). Returns the address or 0 on miss.
    // ─────────────────────────────────────────────────────────────────────────
    static uint64_t FindUClassByName(const char* className)
    {
        if (!UE::FName::FNameCharConstructor) return 0;

        UE::FName fn;
        UE::FName::FNameCharConstructor(&fn, className, UE::EFindName::FNAME_Find);
        if (fn.Index == 0) return 0;

        uint64_t gameBase = (uint64_t)GetModuleHandleA(nullptr);
        IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)gameBase;
        IMAGE_NT_HEADERS* nt  = (IMAGE_NT_HEADERS*)(gameBase + dos->e_lfanew);
        uint64_t moduleEnd = gameBase + nt->OptionalHeader.SizeOfImage;

        SYSTEM_INFO si; GetSystemInfo(&si);
        uint64_t addr = (uint64_t)si.lpMinimumApplicationAddress;
        uint64_t maxAddr = (uint64_t)si.lpMaximumApplicationAddress;
        MEMORY_BASIC_INFORMATION mbi;

        while (addr < maxAddr) {
            if (VirtualQuery((void*)addr, &mbi, sizeof(mbi)) == 0) break;
            if (mbi.State == MEM_COMMIT &&
                (mbi.Protect == PAGE_READWRITE || mbi.Protect == PAGE_READONLY) &&
                mbi.RegionSize >= 0x100 && mbi.RegionSize < 0x10000000)
            {
                uint64_t rb = (uint64_t)mbi.BaseAddress;
                uint64_t re = rb + mbi.RegionSize;
                if (rb < gameBase || rb >= moduleEnd) {
                    __try {
                        for (uint64_t p = rb; p + 0x40 < re; p += 8) {
                            int32_t nameIdx = *(int32_t*)(p + 0x20);
                            int32_t nameNum = *(int32_t*)(p + 0x24);
                            if (nameIdx != fn.Index || nameNum != 0) continue;

                            uint64_t vt = *(uint64_t*)p;
                            if ((vt & 0xF) != 0 || vt < gameBase || vt >= moduleEnd) continue;

                            return p;
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {}
                }
            }
            addr = (uint64_t)mbi.BaseAddress + mbi.RegionSize;
        }
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FindInstanceOfClass — heap scans for any UObject whose ClassPrivate
    // (or any ancestor in the SuperStruct chain) matches the given UClass.
    // Skips Default__ CDOs. Returns the instance address or nullptr on miss.
    // ─────────────────────────────────────────────────────────────────────────
    static void* FindInstanceOfClass(uint64_t targetClassAddr)
    {
        if (targetClassAddr == 0) return nullptr;

        uint64_t gameBase = (uint64_t)GetModuleHandleA(nullptr);
        IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)gameBase;
        IMAGE_NT_HEADERS* nt  = (IMAGE_NT_HEADERS*)(gameBase + dos->e_lfanew);
        uint64_t moduleEnd = gameBase + nt->OptionalHeader.SizeOfImage;

        auto classInherits = [&](uint64_t classPtr) -> bool {
            __try {
                for (int depth = 0; depth < 20; depth++) {
                    if (classPtr == 0) return false;
                    if (classPtr == targetClassAddr) return true;
                    classPtr = *(uint64_t*)(classPtr + 0x50);
                }
            } __except(EXCEPTION_EXECUTE_HANDLER) {}
            return false;
        };

        SYSTEM_INFO si; GetSystemInfo(&si);
        uint64_t addr = (uint64_t)si.lpMinimumApplicationAddress;
        uint64_t maxAddr = (uint64_t)si.lpMaximumApplicationAddress;
        MEMORY_BASIC_INFORMATION mbi;

        while (addr < maxAddr) {
            if (VirtualQuery((void*)addr, &mbi, sizeof(mbi)) == 0) break;
            if (mbi.State == MEM_COMMIT &&
                (mbi.Protect == PAGE_READWRITE || mbi.Protect == PAGE_READONLY) &&
                mbi.RegionSize >= 0x100 && mbi.RegionSize < 0x10000000)
            {
                uint64_t rb = (uint64_t)mbi.BaseAddress;
                uint64_t re = rb + mbi.RegionSize;
                if (rb < gameBase || rb >= moduleEnd) {
                    __try {
                        for (uint64_t p = rb; p + 0x80 < re; p += 8) {
                            uint64_t vt = *(uint64_t*)p;
                            if (vt < gameBase || vt >= moduleEnd) continue;
                            uint64_t cp = *(uint64_t*)(p + 0x18);
                            if (cp == 0 || cp < 0x10000) continue;
                            if (cp != targetClassAddr && !classInherits(cp)) continue;

                            // Skip CDOs
                            int32_t nameIdx = *(int32_t*)(p + 0x20);
                            char nameBuf[128] = {};
                            ResolveFName(nameIdx, nameBuf, sizeof(nameBuf));
                            if (strncmp(nameBuf, "Default__", 9) == 0) continue;

                            return (void*)p;
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {}
                }
            }
            addr = (uint64_t)mbi.BaseAddress + mbi.RegionSize;
        }
        return nullptr;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FindUFunctionByNameOnClass — heap scans for a UFunction matching both
    // the given function FName AND owning class FName. UFunction objects have
    // a vtable at +0x00, NamePrivate at +0x20, and OuterPrivate (UClass*) at
    // +0x28. Returns the UFunction address or nullptr.
    //
    // We require the owner to match because in MVS multiple classes declare
    // the same UFunction name (e.g. HandleGameplayConfigParsedOrReturnToLobby
    // exists on both SelectingPerks and ArenaShop) and an unfiltered scan
    // would non-deterministically return whichever the heap allocator put
    // first.
    //
    // Critically, we also filter by the KNOWN UFunction vtable RVAs. Without
    // this check, ANY UObject with a matching FName index could be returned
    // (e.g. a UProperty or UClass with the same name), and passing a non-
    // UFunction pointer to ProcessEvent crashes the game. These RVAs were
    // discovered from static analysis of the binary:
    //   0x6795E20  — native UFunction vtable
    //   0x6795A90  — blueprint-generated UFunction vtable
    // ─────────────────────────────────────────────────────────────────────────
    constexpr uint64_t UFUNCTION_VTABLE_RVA_NATIVE = 0x6795E20;
    constexpr uint64_t UFUNCTION_VTABLE_RVA_BP     = 0x6795A90;

    static void* FindUFunctionByNameOnClass(const char* funcName,
                                             const char* ownerClassName,
                                             uint64_t* outClassAddr)
    {
        if (!UE::FName::FNameCharConstructor) return nullptr;

        UE::FName fnFunc;
        UE::FName::FNameCharConstructor(&fnFunc, funcName, UE::EFindName::FNAME_Find);
        if (fnFunc.Index == 0) return nullptr;

        UE::FName fnClass;
        UE::FName::FNameCharConstructor(&fnClass, ownerClassName, UE::EFindName::FNAME_Find);
        if (fnClass.Index == 0) return nullptr;

        uint64_t gameBase = (uint64_t)GetModuleHandleA(nullptr);
        IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)gameBase;
        IMAGE_NT_HEADERS* nt  = (IMAGE_NT_HEADERS*)(gameBase + dos->e_lfanew);
        uint64_t moduleEnd = gameBase + nt->OptionalHeader.SizeOfImage;

        SYSTEM_INFO si; GetSystemInfo(&si);
        uint64_t addr = (uint64_t)si.lpMinimumApplicationAddress;
        uint64_t maxAddr = (uint64_t)si.lpMaximumApplicationAddress;
        MEMORY_BASIC_INFORMATION mbi;

        while (addr < maxAddr) {
            if (VirtualQuery((void*)addr, &mbi, sizeof(mbi)) == 0) break;
            if (mbi.State == MEM_COMMIT &&
                (mbi.Protect == PAGE_READWRITE || mbi.Protect == PAGE_READONLY) &&
                mbi.RegionSize >= 0x100 && mbi.RegionSize < 0x10000000)
            {
                uint64_t rb = (uint64_t)mbi.BaseAddress;
                uint64_t re = rb + mbi.RegionSize;
                if (rb < gameBase || rb >= moduleEnd) {
                    __try {
                        for (uint64_t p = rb; p + 0x100 < re; p += 8) {
                            int32_t fnIdx = *(int32_t*)(p + 0x20);
                            int32_t fnNum = *(int32_t*)(p + 0x24);
                            if (fnIdx != fnFunc.Index || fnNum != 0) continue;

                            uint64_t vt = *(uint64_t*)p;
                            if (vt < gameBase || vt >= moduleEnd) continue;

                            // MUST be one of the known UFunction vtables.
                            // Otherwise we could return a UProperty or UClass
                            // that happens to share the same FName, and
                            // ProcessEvent would crash the game.
                            uint64_t vtRva = vt - gameBase;
                            if (vtRva != UFUNCTION_VTABLE_RVA_NATIVE &&
                                vtRva != UFUNCTION_VTABLE_RVA_BP) continue;

                            // OuterPrivate at +0x28 is the owning UClass
                            uint64_t outer = *(uint64_t*)(p + 0x28);
                            if (outer < 0x10000 || outer > 0x00007FFFFFFFFFFFULL) continue;

                            // Check the class FName matches
                            int32_t outerNameIdx = 0;
                            __try { outerNameIdx = *(int32_t*)(outer + 0x20); }
                            __except(EXCEPTION_EXECUTE_HANDLER) { continue; }
                            if (outerNameIdx != fnClass.Index) continue;

                            if (outClassAddr) *outClassAddr = outer;
                            return (void*)p;
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {}
                }
            }
            addr = (uint64_t)mbi.BaseAddress + mbi.RegionSize;
        }
        return nullptr;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CancelMatchTimerProc — runs on the GAME THREAD (via SetTimer on the
    // game window) so that ProcessEvent and FText construction are safe.
    //
    // 1. Find AMvsPreMatchGameState actor (the pre-match game state actor).
    //    If it doesn't exist on the heap, the player is in active gameplay
    //    (post-faceoff) → no-op so we don't kick a player out of their match.
    // 2. Read its StateMachine field (offset +0x320 per SDK).
    // 3. Read CurrentState (offset +0x58 of UPfgStateMachineBase).
    // 4. Resolve CurrentState's class name via FName.
    // 5. Dispatch to the matching UFunction via ProcessEvent:
    //      SelectingPerks*       → HandleGameplayConfigParsedOrReturnToLobby(false)
    //      TransitioningToGameplay → HandleTransitionToGameplayFailed()
    //      PerksLocked           → no-op (state auto-advances quickly)
    //      ArenaShop*            → HandleGameplayConfigParsedOrReturnToLobby(false) (arena, disabled)
    //
    // IMPORTANT: This function MUST run on the game thread, not the poller
    // background thread. ProcessEvent touches UE state that is only safe
    // from the main thread; cross-thread calls corrupt state and crash. The
    // caller (TriggerStateAwareCancelMatch) schedules this via SetTimer on
    // the UnrealWindow, and Win32 dispatches the callback on the game
    // thread (the same thread that processes the game's message pump).
    // ─────────────────────────────────────────────────────────────────────────
    #define OVS_CANCEL_MATCH_TIMER_ID 0x0F5B

    static void DoStateAwareCancelMatchOnGameThread()
    {
        printf("[CancelMatch] Starting state-aware cancel (game thread)\n");

        // Step 1: locate the pre-match game state actor
        uint64_t preMatchGsClass = FindUClassByName("MvsPreMatchGameState");
        void* preMatchGs = preMatchGsClass ? FindInstanceOfClass(preMatchGsClass) : nullptr;
        if (!preMatchGs) {
            printf("[CancelMatch] No AMvsPreMatchGameState — player is in active gameplay, ignoring (WS + match intact)\n");
            return;
        }

        // Step 2: StateMachine pointer at +0x320
        uint64_t stateMachine = 0;
        __try { stateMachine = *(uint64_t*)((uint64_t)preMatchGs + 0x320); }
        __except(EXCEPTION_EXECUTE_HANDLER) {}
        if (!stateMachine || stateMachine < 0x10000 || stateMachine > 0x00007FFFFFFFFFFFULL) {
            printf("[CancelMatch] StateMachine ptr invalid (%p)\n", (void*)stateMachine);
            return;
        }

        // Step 3: CurrentState pointer at +0x58
        uint64_t currentState = 0;
        __try { currentState = *(uint64_t*)(stateMachine + 0x58); }
        __except(EXCEPTION_EXECUTE_HANDLER) {}
        if (!currentState || currentState < 0x10000 || currentState > 0x00007FFFFFFFFFFFULL) {
            printf("[CancelMatch] CurrentState ptr invalid (%p) — no-op\n", (void*)currentState);
            return;
        }

        // Step 4: read CurrentState's class FName and resolve to a string
        uint64_t curClass = 0;
        __try { curClass = *(uint64_t*)(currentState + 0x18); }
        __except(EXCEPTION_EXECUTE_HANDLER) {}
        char className[128] = {};
        if (curClass) {
            int32_t classNameIdx = 0;
            __try { classNameIdx = *(int32_t*)(curClass + 0x20); }
            __except(EXCEPTION_EXECUTE_HANDLER) {}
            if (classNameIdx > 0) ResolveFName(classNameIdx, className, sizeof(className));
        }
        printf("[CancelMatch] CurrentState=%p class='%s'\n", (void*)currentState, className);

        // Step 5: pick the dispatch target by class name (partial match so
        // BP-generated subclasses with _C_ suffix still hit)
        struct DispatchTarget {
            const char* ufuncName;
            const char* ownerClassName;
            bool        takesBoolArg;
        } target = { nullptr, nullptr, false };

        // Simplified dispatch: always use HandleTransitionToGameplayFailed
        // on MvsPreMatchTransitioningToGameplayState. This handler sends
        // the player back to the menu regardless of how they got into the
        // pregame state machine. Keeping this as the single path.
        //
        // (The SelectingPerks / PerksSelected / ArenaShop / PerksLocked
        // branches are intentionally disabled — see comment block below.)
        target = { "HandleTransitionToGameplayFailed",
                   "MvsPreMatchTransitioningToGameplayState", false };

        // --- Legacy dispatch branches, disabled ---
        // if (strstr(className, "SelectingPerks") || strstr(className, "PerksSelected")) {
        //     target = { "HandleGameplayConfigParsedOrReturnToLobby",
        //                "MvsPreMatchSelectingPerksBaseState", true };
        // }
        // else if (strstr(className, "TransitioningToGameplay")) {
        //     target = { "HandleTransitionToGameplayFailed",
        //                "MvsPreMatchTransitioningToGameplayState", false };
        // }
        // else if (strstr(className, "PerksLocked")) {
        //     printf("[CancelMatch] CurrentState is PerksLocked — letting natural flow proceed\n");
        //     return;
        // }
        // else if (strstr(className, "ArenaShop") || strstr(className, "ArenaCharacterSelect")) {
        //     target = { "HandleGameplayConfigParsedOrReturnToLobby",
        //                "MvsPreMatchArenaShopBaseState", true };
        // }
        // else {
        //     printf("[CancelMatch] Unrecognized state class '%s' — no-op\n", className);
        //     return;
        // }

        // Step 6: resolve the UFunction via FName lookup, then ProcessEvent
        uint64_t ownerClassAddr = 0;
        void* ufunc = FindUFunctionByNameOnClass(target.ufuncName, target.ownerClassName, &ownerClassAddr);
        if (!ufunc) {
            printf("[CancelMatch] UFunction '%s' on '%s' not found — no-op\n",
                   target.ufuncName, target.ownerClassName);
            return;
        }

        printf("[CancelMatch] Invoking %s::%s via ProcessEvent on state %p\n",
               target.ownerClassName, target.ufuncName, (void*)currentState);

        // Param: bool bSuccess (1 byte) for functions that take one
        uint8_t params[8] = {};
        params[0] = 0; // bSuccess = false → "go to failure path"

        __try {
            uint64_t gameBase = (uint64_t)GetModuleHandleA(nullptr);
            ProcessEventFn pe = (ProcessEventFn)(gameBase + RVA_PROCESS_EVENT);
            pe((void*)currentState, ufunc, target.takesBoolArg ? (void*)params : nullptr);
            printf("[CancelMatch] ProcessEvent returned cleanly — player should be transitioning\n");
        } __except(EXCEPTION_EXECUTE_HANDLER) {
            printf("[CancelMatch] ProcessEvent threw 0x%08X\n", GetExceptionCode());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Win32 timer callback — runs on the game thread (via SetTimer on
    // UnrealWindow, dispatched by the game's own message pump).
    // ─────────────────────────────────────────────────────────────────────────
    static VOID CALLBACK CancelMatchTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD)
    {
        KillTimer(hwnd, id);
        DoStateAwareCancelMatchOnGameThread();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TriggerStateAwareCancelMatch — called from the poller background
    // thread when a match_cancel notification arrives. We do NOT run the
    // cancel logic inline here because ProcessEvent and UE state access are
    // only safe on the game thread. Instead, schedule a Win32 timer on the
    // game window which fires the callback on the game thread ~200ms later.
    // ─────────────────────────────────────────────────────────────────────────
    static void TriggerStateAwareCancelMatch()
    {
        HWND gameWnd = FindWindowA("UnrealWindow", nullptr);
        if (!gameWnd) {
            printf("[CancelMatch] UnrealWindow not found — cannot schedule cancel\n");
            return;
        }
        SetTimer(gameWnd, OVS_CANCEL_MATCH_TIMER_ID, 200, CancelMatchTimerProc);
        printf("[CancelMatch] Scheduled match cancel on game thread\n");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ParseUrl — split "http://host:port" into (host, port). Drops scheme and
    // any trailing path. Returns false on parse failure.
    // ─────────────────────────────────────────────────────────────────────────
    static bool ParseUrl(const std::string& url, std::string& outHost, int& outPort)
    {
        outHost.clear();
        outPort = 8000;

        const char* p = url.c_str();
        if (strncmp(p, "http://", 7) == 0) p += 7;
        else if (strncmp(p, "https://", 8) == 0) p += 8;

        const char* hostStart = p;
        while (*p && *p != ':' && *p != '/') p++;
        if (p == hostStart) return false;
        outHost.assign(hostStart, p);

        if (*p == ':') {
            p++;
            outPort = atoi(p);
            while (*p && *p != '/') p++;
        }
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HttpGet — synchronous GET to host:port/path. Returns body length, or
    // -1 on failure. Body is written to outBuf (null-terminated).
    // ─────────────────────────────────────────────────────────────────────────
    static int HttpGet(const char* host, int port, const char* path,
                       char* outBuf, int outBufSize)
    {
        if (outBuf && outBufSize > 0) outBuf[0] = '\0';

        wchar_t whost[256];
        MultiByteToWideChar(CP_UTF8, 0, host, -1, whost, 256);
        wchar_t wpath[512];
        MultiByteToWideChar(CP_UTF8, 0, path, -1, wpath, 512);

        HINTERNET hSession = WinHttpOpen(L"OpenVersus/1.0",
            WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
        if (!hSession) return -1;

        WinHttpSetTimeouts(hSession, 2000, 2000, 2000, 2000);

        HINTERNET hConnect = WinHttpConnect(hSession, whost, (INTERNET_PORT)port, 0);
        if (!hConnect) { WinHttpCloseHandle(hSession); return -1; }

        HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"GET", wpath, NULL,
            WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0);
        if (!hRequest) {
            WinHttpCloseHandle(hConnect);
            WinHttpCloseHandle(hSession);
            return -1;
        }

        int total = -1;
        BOOL bResults = WinHttpSendRequest(hRequest,
            WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0);
        if (bResults) bResults = WinHttpReceiveResponse(hRequest, NULL);

        if (bResults && outBuf) {
            total = 0;
            DWORD dwSize = 0;
            do {
                dwSize = 0;
                if (!WinHttpQueryDataAvailable(hRequest, &dwSize)) break;
                if (dwSize == 0) break;
                int avail = outBufSize - total - 1;
                if (avail <= 0) break;
                DWORD toRead = (dwSize > (DWORD)avail) ? (DWORD)avail : dwSize;
                DWORD dwRead = 0;
                if (!WinHttpReadData(hRequest, outBuf + total, toRead, &dwRead)) break;
                total += dwRead;
                outBuf[total] = '\0';
                if (dwRead == 0) break;
            } while (dwSize > 0);
        }

        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return total;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JsonGetString — extract a string field from a flat JSON snippet.
    // Looks for "key":"value". Handles basic backslash escapes. Writes to
    // outBuf (null-terminated). Returns true on success.
    // ─────────────────────────────────────────────────────────────────────────
    static bool JsonGetString(const char* json, const char* key,
                              char* outBuf, int outBufSize)
    {
        if (!json || !key || !outBuf || outBufSize < 2) return false;
        outBuf[0] = '\0';

        char needle[64];
        int n = snprintf(needle, sizeof(needle), "\"%s\"", key);
        if (n < 0 || n >= (int)sizeof(needle)) return false;

        const char* p = strstr(json, needle);
        if (!p) return false;
        p += strlen(needle);
        while (*p == ' ' || *p == '\t' || *p == ':') p++;
        if (*p != '"') return false;
        p++;

        int i = 0;
        while (*p && *p != '"' && i < outBufSize - 1) {
            if (*p == '\\' && p[1]) {
                p++;
                switch (*p) {
                    case 'n':  outBuf[i++] = '\n'; break;
                    case 't':  outBuf[i++] = '\t'; break;
                    case '"':  outBuf[i++] = '"';  break;
                    case '\\': outBuf[i++] = '\\'; break;
                    default:   outBuf[i++] = *p;   break;
                }
                p++;
            } else {
                outBuf[i++] = *p++;
            }
        }
        outBuf[i] = '\0';
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JsonGetFloat — extract a numeric field. Returns parsed double or
    // `defaultValue` on miss.
    // ─────────────────────────────────────────────────────────────────────────
    static double JsonGetFloat(const char* json, const char* key, double defaultValue)
    {
        if (!json || !key) return defaultValue;
        char needle[64];
        int n = snprintf(needle, sizeof(needle), "\"%s\"", key);
        if (n < 0 || n >= (int)sizeof(needle)) return defaultValue;
        const char* p = strstr(json, needle);
        if (!p) return defaultValue;
        p += strlen(needle);
        while (*p == ' ' || *p == '\t' || *p == ':') p++;
        return atof(p);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SafeGetDefaultWidgetClass — reads the notification manager's default
    // widget class pointer at +0xA0. Without setting
    // FMvsNotificationData.WidgetClass to this value, RequestShowNotification
    // silently no-ops because the notification has no widget template.
    // ─────────────────────────────────────────────────────────────────────────
    static uint64_t SafeGetDefaultWidgetClass()
    {
        __try {
            if (!MVSGame::Instances::FighterGame) return 0;
            auto* mgr = MVSGame::UMvsNotificationManager::Get(MVSGame::Instances::FighterGame);
            if (!mgr) return 0;
            return *(uint64_t*)((uint64_t)mgr + 0xA0);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Game-thread deferral for notifications.
    //
    // RequestShowNotification must be invoked on the game thread (the same
    // thread that processes the UE main loop / widget system). Our poller
    // runs on a detached background thread and calling into FText /
    // notification-manager from there silently no-ops — the notification
    // queues to a widget system that never flushes because it's not on the
    // game thread.
    //
    // To fix this, we use the same trick CancelMatchTimerProc uses: stash
    // the request in module globals and call Win32 SetTimer on the game's
    // UnrealWindow. The game's message loop picks up the timer and runs the
    // callback on the game thread, where the notification manager is safe
    // to call.
    //
    // Only one pending banner is queued at a time. If a second arrives
    // while the first is still pending, the earlier request gets replaced
    // (which is fine for an admin broadcast — there's no point in stacking).
    // ─────────────────────────────────────────────────────────────────────────
    #define OVS_BANNER_TIMER_ID 0x0B5A  // arbitrary id

    struct PendingBanner {
        wchar_t title[256];
        wchar_t message[512];
        float   timeoutSec;
        bool    pending;
    };
    static PendingBanner g_PendingBanner = {};

    // Runs on the game thread (via SetTimer on UnrealWindow).
    static VOID CALLBACK BannerTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD)
    {
        KillTimer(hwnd, id);

        if (!g_PendingBanner.pending) return;
        g_PendingBanner.pending = false;

        using namespace MVSGame;

        if (!Instances::FighterGame) {
            printf("[NotifPoller] Banner (game thread): FighterGame not initialized\n");
            return;
        }
        auto* mgr = UMvsNotificationManager::Get(Instances::FighterGame);
        if (!mgr) {
            printf("[NotifPoller] Banner (game thread): notification manager unavailable\n");
            return;
        }

        uint64_t widgetClass = SafeGetDefaultWidgetClass();
        if (!widgetClass) {
            printf("[NotifPoller] Banner (game thread): default WidgetClass unresolved\n");
            return;
        }

        __try {
            FMvsNotificationData data;
            if (g_PendingBanner.title[0])   data.Text    = FText(g_PendingBanner.title);
            if (g_PendingBanner.message[0]) data.Caption = FText(g_PendingBanner.message);
            data.TimeoutSeconds = g_PendingBanner.timeoutSec > 0 ? g_PendingBanner.timeoutSec : 8.0f;
            data.bAutoDismissWhenClicked = true;
            data.Category = EMvsNotificationCategory::None;
            data.WidgetClass = widgetClass;
            // ActionButtonText intentionally left empty — no View button.

            mgr->RequestShowNotification(&data);

            printf("[NotifPoller] Banner shown on game thread (widgetClass=%p)\n",
                   (void*)widgetClass);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            printf("[NotifPoller] Banner: exception 0x%08X on game thread\n",
                   GetExceptionCode());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ShowBanner — called from the poll loop (background thread). Copies the
    // request into g_PendingBanner and schedules the game-thread callback.
    // ─────────────────────────────────────────────────────────────────────────
    static void ShowBanner(const char* title, const char* message, float timeoutSec)
    {
        // Copy into the pending slot.
        memset(&g_PendingBanner, 0, sizeof(g_PendingBanner));
        if (title)   MultiByteToWideChar(CP_UTF8, 0, title,   -1, g_PendingBanner.title,   256);
        if (message) MultiByteToWideChar(CP_UTF8, 0, message, -1, g_PendingBanner.message, 512);
        g_PendingBanner.timeoutSec = timeoutSec;
        g_PendingBanner.pending    = true;

        // Kick off the Win32 timer on the game window — the callback fires
        // on the game thread where notification manager calls are safe.
        HWND gameWnd = FindWindowA("UnrealWindow", nullptr);
        if (!gameWnd) {
            printf("[NotifPoller] Banner: UnrealWindow not found — cannot defer\n");
            g_PendingBanner.pending = false;
            return;
        }
        SetTimer(gameWnd, OVS_BANNER_TIMER_ID, 50, BannerTimerProc);

        printf("[NotifPoller] Banner queued: top='%s' bottom='%s' timeout=%.1fs (deferred to game thread)\n",
               title ? title : "",
               message ? message : "",
               timeoutSec);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DispatchNotification — handle one parsed JSON object. Reads "type" and
    // routes to the appropriate handler.
    // ─────────────────────────────────────────────────────────────────────────
    static void DispatchNotification(const char* objJson)
    {
        char type[64]    = { 0 };
        char title[256]  = { 0 };
        char message[512] = { 0 };

        JsonGetString(objJson, "type",    type,    sizeof(type));
        JsonGetString(objJson, "title",   title,   sizeof(title));
        JsonGetString(objJson, "message", message, sizeof(message));

        if (type[0] == '\0') return;

        if (strcmp(type, "match_cancel") == 0) {
            printf("[NotifPoller] Match cancel received: %s — %s\n", title, message);
            // Pop a banner so the user knows what just happened. Uses the
            // game-thread-deferred ShowBanner helper so WidgetClass is set
            // correctly (the old OVS::ShowNotification path silently no-oped
            // because it ran on the poller thread without WidgetClass).
            ShowBanner("Match Cancelled", "Opponent left the match", 5.0f);
            // Walk the state machine and dispatch to the appropriate UE handler.
            TriggerStateAwareCancelMatch();
        }
        else if (strcmp(type, "admin_banner") == 0) {
            double timeout = JsonGetFloat(objJson, "timeout", 8.0);
            ShowBanner(title, message, (float)timeout);
        }
        else {
            // Unknown type — log and ignore so the server can add new types
            // without breaking older clients.
            printf("[NotifPoller] Unknown notification type '%s' — ignoring\n", type);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PollerThreadFunc — the polling loop. Runs on a detached background
    // thread until g_Running is cleared.
    // ─────────────────────────────────────────────────────────────────────────
    static void PollerThreadFunc()
    {
        // Wait for game hooks to fully initialize before polling.
        Sleep(8000);

        printf("[NotifPoller] Polling %s:%d/ovs/notifications every 2s\n",
               g_ServerHost.c_str(), g_ServerPort);

        static char responseBuf[8192];
        int failCount = 0;

        while (g_Running.load())
        {
            int bodyLen = HttpGet(g_ServerHost.c_str(), g_ServerPort,
                                  "/ovs/notifications", responseBuf, sizeof(responseBuf));

            if (bodyLen < 0) {
                failCount++;
                if (failCount == 1 || (failCount % 30) == 0) {
                    printf("[NotifPoller] Poll failed (count=%d)\n", failCount);
                }
            }
            else if (bodyLen >= 2 && responseBuf[0] == '[') {
                failCount = 0;
                // Walk objects in the JSON array. We don't bother with a real
                // parser — just look for {"type" boundaries.
                char* cursor = responseBuf;
                while ((cursor = strstr(cursor, "{\"type\"")) != nullptr) {
                    char* end = strchr(cursor, '}');
                    if (!end) break;
                    char saved = end[1];
                    end[1] = '\0';

                    DispatchNotification(cursor);

                    end[1] = saved;
                    cursor = end + 1;
                }
            }

            // ~2 second sleep, but check the running flag periodically so
            // shutdown is responsive.
            for (int i = 0; i < 20 && g_Running.load(); i++) {
                Sleep(100);
            }
        }

        printf("[NotifPoller] Thread exiting\n");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────
    void Start(const std::string& serverUrl)
    {
        if (g_Running.load()) {
            printf("[NotifPoller] Already running\n");
            return;
        }

        if (!ParseUrl(serverUrl, g_ServerHost, g_ServerPort)) {
            printf("[NotifPoller] Failed to parse server URL: %s\n", serverUrl.c_str());
            return;
        }

        g_Running.store(true);
        g_Thread = std::thread(PollerThreadFunc);
        g_Thread.detach();
        printf("[NotifPoller] Started\n");
    }

    void Stop()
    {
        g_Running.store(false);
    }

} // namespace OVS::NotificationPoller
