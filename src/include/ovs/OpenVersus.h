#pragma once
#ifndef OVS_H
#define OVS_H

#include "includes.h"
#include "constants.hpp"
#include "Trampoline/Trampoline.h"
#include <unordered_map>
#include "mvs/mvs.h"
#include "ovs/OVSUtils.h"
#include <string>
#include <ostream>
#include <map>

using namespace UE;

namespace OVS {

    // OVSSetting is now defined in constants.hpp

    extern OVSSetting OVSDefaultSettingsArray[];
    extern const size_t OVSDefaultSettingsArrayCount;
    OVSSetting GetOVSSettingByName(const wchar_t* name);
    void ShowCredits();

    namespace Proxies {
        //__int64									__fastcall	ReadFString(__int64, __int64);
        //MVSGame::FName*							__fastcall	ReadFNameToWStr(MVSGame::FName&, char*);
        HANDLE									__stdcall	CreateFile(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode, LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition, DWORD dwFlagsAndAttributes, HANDLE hTemplateFile);
        const char**							__fastcall	OverrideGameEndpoint(int64_t*, const wchar_t*);
        int64_t*								__fastcall	OverrideProdEndpoint(int64_t*, const wchar_t*);
        bool									__fastcall  OVSOfflineModeChecker(int32_t*);
        MVSGame::UFighterGameInstance*			__fastcall CopyFighterInstance(MVSGame::UFighterGameInstance*, uint64_t*);
    };

    namespace Hooks {
        bool DisableSignatureCheck(Trampoline*);
        bool OverrideGameEndpointsData(Trampoline*);
        bool OverrideProdEndpointsData(Trampoline*);
        bool PatchSunsetSetterIntoOVSChecker(Trampoline*);
        bool DialogHooks(Trampoline*);
        bool HookUEFuncs(Trampoline*);
        bool NotificationHooks(Trampoline*);
    };

    extern bool IsOVSUpdateRequired;

    template<typename T>
    MVSGame::UMvsDialog* ShowDialog(const T* promptText, const T* description = nullptr, const T* button1 = nullptr, const T* button2 = nullptr, const T* button3 = nullptr, const int selectedButton = -1, bool ShowExitButton = false, bool ShowSpinner = false, bool ShowSolidBackground = true, bool HideActionBar = false);

    template<typename T>
    MVSGame::UMvsNotificationManager* ShowNotification(const T* text = nullptr, const T* caption = nullptr, const float timeout = 5.0f, const bool bAutoDismissWhenClicked = true);

    template<typename T>
    MVSGame::UMvsNotificationManager* ShowNotification(const T* text, const T* caption, const float timeout, const bool bAutoDismissWhenClicked)
    {
        using namespace MVSGame;

        UMvsNotificationManager* manager = UMvsNotificationManager::Get(Instances::FighterGame);
        if (!manager)
            return nullptr;

        MVSGame::FMvsNotificationData params;

        if (text)
            params.Text = FText(text);
        if (caption)
            params.Caption = FText(caption);
        //if (actionText)
            //params.ActionButtonText = FText(actionText);

        params.TimeoutSeconds = timeout;
        params.bAutoDismissWhenClicked = bAutoDismissWhenClicked;

        params.Category = MVSGame::EMvsNotificationCategory::None;

        manager->RequestShowNotification(&params);
        return manager;
    }

    template<typename T>
    MVSGame::UMvsDialog* ShowDialog(
        const T* promptText, const T* description, const T* button1, const T* button2, const T* button3, const int selectedButton,
        bool ShowExitButton, bool ShowSpinner, bool ShowSolidBackground, bool HideActionBar
    )
    {
        using namespace MVSGame;

        UMvsFrontendManager* frontend = GetFrontendManager(Instances::FighterGame);
        if (!frontend || !frontend->CurrentStateWidget)
            return nullptr;

        FMvsDialogParameters DialogParameters;
        DialogParameters.PromptText = FText(promptText);

        if (description)
            DialogParameters.PromptDescriptionText = FText(description);

        if (button1)
            DialogParameters.ButtonOneText = FText(button1);

        if (button2)
            DialogParameters.ButtonTwoText = FText(button2);

        if (button3)
            DialogParameters.ButtonThreeText = FText(button3);

        if (selectedButton != -1)
        {
            DialogParameters.ButtonToFocus.Value = selectedButton;
            DialogParameters.ButtonToFocus.bIsSet = true;
        }
        else
        {
            DialogParameters.ButtonToFocus.bIsSet = false;
        }

        DialogParameters.bShowExitButton = ShowExitButton;
        DialogParameters.bShowSpinner = ShowSpinner;
        DialogParameters.bShowSolidBackground = ShowSolidBackground;
        DialogParameters.bHideActionBar = HideActionBar;

        return frontend->AddDialog(&DialogParameters);
    }

}

namespace HookMetadata { //Namespace for helpers for game functions
    struct ActiveMods {
        bool bAntiSigCheck		= false;
        bool bGameEndpointSwap	= false;
        bool bProdEndpointSwap  = false;
        bool bSunsetDate		= false;
        bool bUEFuncs			= false;
        bool bDialog			= false;
        bool bNotifs			= false;
    };

    struct LibMapsStruct {
        LibFuncStruct ModLoader;
        LibFuncStruct AntiCheatEngine;
        LibFuncStruct CurlSetOpt;
        LibFuncStruct CurlPerform;
    };

    extern ActiveMods			sActiveMods;
    extern LibMapsStruct		sLFS;
    extern HHOOK				KeyboardProcHook;
    extern HMODULE				CurrentDllModule;
    extern HANDLE				Console;
};
#endif // OVS_H