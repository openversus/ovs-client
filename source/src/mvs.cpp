#include "OVSUtils.h"
#include "mvs.h"

namespace MVSGame {
    // Game Functions
    GetEndpointKeyValueType*		GetEndpointKeyValue				= nullptr;
    SetFStringValueType*			SetFStringValue					= nullptr;
    FDateTimeType*					FDateTime						= nullptr;
    InitThreadHeaderType*			Init_thread_header				= nullptr;
    InitThreadFooterType*			Init_thread_footer				= nullptr;
    DialogParamsType*				FMvsDialogParametersConst		= nullptr;
    //AddDialogUtilType*				AddDialogUtil					= nullptr;
    //AddDialogFrontType*				AddDialogFront					= nullptr;
    UFighterGameInstanceConstType*	UFigherGameInstanceConst		= nullptr;
    GetFrontendManagerType*			GetFrontendManager				= nullptr;
    SingleParamDialogCallbackSetterType* SingleParamDialogCallbackSetter = nullptr;
    QuitGameParamsType*				QuitGame						= nullptr;
    // Game vars
    uint64_t* kSunsetDate = nullptr;
    FText* GLeave_Lobby_Prompt_Text = nullptr;

    uint64_t UMvsDialog::AssignCallbackToButton(TMulticastDelegateBase* Button, void* MainCallback, void* CleanupCallback)
    {
        uint64_t result;
        MVSGame::UMvsDialog* self = this;
        MVSGame::SingleParamDialogCallbackSetter(Button, &result, self, self);

        auto* Faker = new MVSGame::FakeVFTabler();
        if (CleanupCallback)
            Faker->AddFunction((void*)CleanupCallback, 0); // Slot 0: Cleanup
        if (MainCallback)
            Faker->AddFunction((void*)MainCallback, 10);   // Slot 10: Main

        *Button->InvocationList.Data->DelegateAllocator = *(void**)Faker;

        return result;
    }
}

namespace MVSGame::Instances {
    uint64_t*						ThisWidget						= nullptr;
    MVSGame::UFighterGameInstance*	FighterGame						= nullptr;
}

namespace UE {
    wchar_t* FString::GetStr()
    {
        return (wchar_t*)(&Data[0]);
    }

    FString* FName::ToString(FString* result)
    {
        return ToStringPtr(this, result);
    }

    FName::FName()
    {
        Index = -1;
        Number = 0;
    }

    FName::FName(const char* Name)
    {
        FNameCharConstructor(this, Name, EFindName::FNAME_Add);
    }

    FName::FName(const wchar_t* Name)
    {
        FNameWCharConstructor(this, Name, EFindName::FNAME_Add);
    }

    FText::FText()
    {
        FText* Empty = GetEmpty();
        TextData = Empty->TextData;
        memcpy(Pad, Empty->Pad, sizeof(Empty->Pad));
    }

    FText::FText(const wchar_t* text)
    {
        FName name(text);
        FText::FromName(this, &name);
    }

    FText::FText(const char* text)
    {
        FName name(text);
        FText::FromName(this, &name);
    }

    FText::FText(const FText& other)
    {
        TextData = other.TextData;
        memcpy(Pad, other.Pad, sizeof(Pad));
    }
}