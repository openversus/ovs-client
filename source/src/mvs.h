#pragma once
#include <cppython.h>
#include <map>

namespace UE {
    template <class T>
    class TArray
    {
    public:
        T* Data;
        int Count;
        int Max;

        //TArray();
        T& Get(int id) { return this->Data[id]; }
        //void Add(T data);

        TArray()
        {
            Data = new T[4];
            Count = 0;
            Max = 4;
        }

        void Add(T InputData)
        {
            if (Count + 1 > Max)
                return;
            Data[Count++] = InputData;
        }
    };


    class FString : public TArray<wchar_t>
    {
    public:
        wchar_t* GetStr();
    };

    enum EFindName
    {
        FNAME_Find,
        FNAME_Add,
        FNAME_Replace,
    };

    class FName
    {
    public:
        int Index;
        int Number;


        FName();
        FName(const char* Name);
        FName(const wchar_t* Name);
        FString* ToString(FString*);

        using ToStringType = FString * (__fastcall*)(FName* This, FString* result);
        static inline ToStringType ToStringPtr = nullptr;

        using FNameConstructorCharType = void(__fastcall*)(FName* This, const char* Name, EFindName FindType);
        static inline FNameConstructorCharType FNameCharConstructor = nullptr;

        using FNameConstructorWCharType = void(__fastcall*)(FName* This, const wchar_t* Name, EFindName FindType);
        static inline FNameConstructorWCharType FNameWCharConstructor = nullptr;
    };

    struct FTextData {
        uint8_t		Pad[0x30];
        FString		TextSource;
    };

    class FText {
    public:
        FTextData* TextData;
        uint8_t		Pad[0x10];

        FText();
        FText(const wchar_t*);
        FText(const char*);
        FText(const FText&);

        using FromStringType = FText * (__fastcall*)(FText*, const FString*);
        static inline FromStringType FromString = nullptr;

        using FromNameType = FText * (__fastcall*)(FText*, FName*);
        static inline FromNameType FromName = nullptr;

        using GetEmptyType = FText * (__fastcall*)(void);
        static inline GetEmptyType GetEmpty = nullptr;
    };

    class FDelegateBase
    {
    public:
        void** DelegateAllocator;
        int DelegateSize;
        int pad;
    };

    class TMulticastDelegateBase
    {
    public:
        TArray<FDelegateBase> InvocationList; // FDelegateBase which is size 0x10
        int CompactionThreshold;
        int InvocationListLockCount;
    };
};

namespace MVSGame { // Namespace for game functions / structs

    using namespace UE;

    struct UNameTableStruct {
        uint32_t	UNK;
        uint32_t	UNK2;
        uint32_t	TablesCount;
        uint32_t	ProbTablesSize;
        FName*		NameTableEntry[];
    };

    struct UNameTableMainStruct {
        uint64_t			ProbablyUNameClassPtr;
        bool				IsInitialized;
        uint8_t				PAD[7];
        UNameTableStruct	UNameTable;
    };

    struct JSONEndpointValue {
        uint64_t*	UnkPointer;
        int32_t		UnkValue; // Usually is `2`
        wchar_t		IntChar[2]; // Either is of size 2, or is of size UnkValue, can't tell yet.
        wchar_t*	ValuePointer;
        int32_t		ValueLength;
        int32_t		ValueLength8BAligned;
    };

    struct DialogFocusButton {
        int Value = 0;
        bool bIsSet = false;
    };

    struct FMvsDialogParameters {
        FText                                   PromptText = FText();
        FText                                   PromptDescriptionText = FText();
        void*									DialogContentClass = 0;
        FText                                   ButtonOneText = FText();
        FText                                   ButtonTwoText = FText();
        FText                                   ButtonThreeText = FText();
        bool                                    bShowSpinner = false;
        bool                                    bShowExitButton = true;
        bool                                    bShowSolidBackground = false;
        bool                                    bHideActionBar = false;
        // Optional
        DialogFocusButton						ButtonToFocus = DialogFocusButton();
    };

    enum class EMvsNotificationCategory : uint8_t {
        None							= 0,
        Social							= 1,
        Reward							= 2,
        System							= 3,
        Matchmaking						= 4,
        Toasts							= 5,
        Debug							= 6,
        MatchmakingErrors				= 7,
        LobbyInvites					= 8,
        EMvsNotificationCategory_MAX,
    };


    struct FMvsNotificationData
    {
        FText Text = FText();                                                            
        FText Caption = FText();
        uint8_t Icon[0x30]{0};
        float TimeoutSeconds = 5.0;
        uint32_t pad = 0;
        uint8_t Action[0x10]{0};
        FText ActionButtonText = FText();
        uint64_t WidgetClass = 0;
        EMvsNotificationCategory Category = EMvsNotificationCategory::None;
        bool bAutoDismissWhenClicked = true;
        uint8_t pad2[6]{ 0 };
        uint64_t* UserData = nullptr;
    };

    class UFighterGameInstance;
    class UMvsNotificationHandle;
    class UMvsNotificationManager
    {
    public:

        UMvsNotificationHandle* RequestShowNotification(const FMvsNotificationData* Data)
        {
            return RequestShowNotificationPtr(this, Data);
        }

        static UMvsNotificationManager* Get(const UFighterGameInstance* WorldContextObject)
        {
            return GetNotifManagerPtr(WorldContextObject);
        }

        UMvsNotificationManager(const UFighterGameInstance* WorldContextObject)
        {
            auto resp = GetNotifManagerPtr(WorldContextObject);
            if (resp)
                *this = *resp;
        }

        using RequestShowNotificationType = UMvsNotificationHandle * (__fastcall*)(UMvsNotificationManager* This, const FMvsNotificationData* Data);
        static inline RequestShowNotificationType RequestShowNotificationPtr = nullptr;

        using GetNotifManagerType = UMvsNotificationManager * (__fastcall*)(const UFighterGameInstance* WorldContextObject);
        static inline GetNotifManagerType GetNotifManagerPtr = nullptr;

    };

    class UMvsDialog
    {
    private:
        uint8_t INHERITED[0x3D8];
    public:
        uint8_t									BP_OnButtonOneClicked[0x10];
        TMulticastDelegateBase					OnButtonOneClicked;
        uint8_t									BP_OnButtonTwoClicked[0x10];
        TMulticastDelegateBase					OnButtonTwoClicked;
        uint8_t									BP_OnButtonThreeClicked[0x10];
        TMulticastDelegateBase					OnButtonThreeClicked;
        TMulticastDelegateBase					OnCancelled;
        uint8_t									OnDismissed[0x10];
        TMulticastDelegateBase					NativeOnDismissed;
    private:
        uint8_t RemainingItems[0x90];


    public:
        uint64_t AssignCallbackToButton(TMulticastDelegateBase*, void* = nullptr, void* = nullptr);
    };

    class UMvsFrontendManager
    {
    private:
        uint8_t INHERITED[0x40]; // 0x00
    public:
        uint64_t* ScreenStack; // 0x40
        uint64_t* ShopPageStack; // 0x48
        uint64_t HoveredButton[10]; // 0x50
        uint64_t OnSceneChangeRequested[2]; // 0xA0
        TMulticastDelegateBase	NativeOnSceneChangeRequested; /// 0xB0
        uint64_t* CurrentStateWidget; // 0xC8
    private:
        uint8_t RemainingItems[0x70];
    public:
        using AddDialogType = UMvsDialog * (__fastcall*)(UMvsFrontendManager*, FMvsDialogParameters*);
        static inline AddDialogType AddDialogPtr = nullptr;

        //using AddNotificationType = uint64_t * (__fastcall*)(UMvsFrontendManager*, FMvsNotificationData*);
        //static inline AddNotificationType AddNotificationPtr = nullptr;

        UMvsDialog* AddDialog(FMvsDialogParameters* Params) { return AddDialogPtr(this, Params); }
        //uint64_t* AddNotification(FMvsNotificationData* Params) { return AddNotificationPtr(this, Params); }
    };

    class FakeVFTabler
    {
    public:
        void** vftable_ptr;

        FakeVFTabler()
        {
            vftable_ptr = new void*[11]();
            for (int i = 0; i < 11; i++)
                vftable_ptr[i] = (void*)&FakeVFTabler::EmptyFunction;
        }

        void AddFunction(void* func, int index)
        {
            vftable_ptr[index] = func;
        }

    private:
        static void __fastcall EmptyFunction() {};
    };

    template <typename T>
    class TArray {
    public:
        T* Data;
        int32_t ElementsCount;
        int32_t Size;
    };

    namespace TArrayHelpers { // Not added to the class because I don't know the correct implementation
        template <typename T>
        T* index(TArray<T> array, int32_t index) {
            int32_t CalcIndex = array.Size * index;
            return &array.Data[index]; // if this doesn't work then I should calculate index manually as index*Size;
        }

    }

    namespace CURL {

        struct HTTPHeaderInstance
        {
            char*		GameVersion;
            uint64_t*	OtherHeaderStuff;
            uint64_t	PAD;
            uint32_t	UNK;
            uint16_t	UNK2[2];
            char		RemainderOfHeader[4]; // it's a string

        };

        struct HTTPHeaderContainer
        {
            uint64_t HeaderSize;
            HTTPHeaderInstance* Instance;
        };

        struct HTTPPostStruct
        {
            HTTPHeaderContainer* HeaderContainer; //0
            char* ResponseBody; //8
            int32_t ResponseBodySize; //10
            int32_t UnknownSize; //14
            char* ResponseStatus; //18
            wchar_t* Unknown; //20
            char* Body; //28
            int32_t BodySize; //30
            int32_t Unknown2; //30
            uint64_t Unknown3;
            uint64_t Unknown4;
            char* HTTPEndpoint;
            uint32_t HTTPEndpointSize;
            uint32_t Unknown2Copy;
            uint64_t Ignore[18];
            char* Unk;
            uint64_t UnkSize;
            char* Platform;
            uint64_t PlatformSize;
            char* Unk2;
            uint64_t Unk2Size;
            char* Platform2;
            uint64_t Platform2Size;
            uint64_t Ignore3[4];
            char* MainEndpoint;
            uint64_t Ignore4;
            char* Endpoint;
            int32_t EndpointSize;
        };

        struct HTTPResponseStruct
        {
            uint64_t* ClassPointer;
            char* ResponseBody;
            uint32_t Sizes[2];
            char* ResponseStatus;
            wchar_t* Unknown;
        };


    };

    
    extern std::map<uint64_t, CURL::HTTPPostStruct*>	CurlObjectMap;

    // Game Functions
    // JSONEndpoint
    typedef			const char**					(__fastcall	GetEndpointKeyValueType)			(int64_t*, const char*);
    typedef			int64_t*						(__fastcall	SetFStringValueType)				(int64_t*, const wchar_t*);
    // Sunset
    typedef			void							(__fastcall FDateTimeType)						(uint64_t*, int, int, int, int, int, int, int);
    typedef			void							(__fastcall InitThreadHeaderType)				(int32_t*);
    typedef			int								(__fastcall InitThreadFooterType)				(int32_t*);
    // Dialog
    typedef			void							(__fastcall DialogParamsType)					(FMvsDialogParameters*, FText*, uint8_t);
    //typedef			uint64_t*						(__fastcall AddDialogUtilType)					(uint64_t*, FMvsDialogParameters*);
    //typedef			UMvsDialog*						(__fastcall AddDialogFrontType)					(UMvsFrontendManager*, FMvsDialogParameters*);
    typedef			UFighterGameInstance*			(__fastcall UFighterGameInstanceConstType)		(UFighterGameInstance*, const uint64_t*);
    typedef			UMvsFrontendManager*			(__fastcall GetFrontendManagerType)				(UFighterGameInstance*);
    typedef			void							(__fastcall SingleParamDialogCallbackSetterType)(TMulticastDelegateBase*, uint64_t*, MVSGame::UMvsDialog*, MVSGame::UMvsDialog*&);
    typedef			bool							(__fastcall QuitGameParamsType)					(UMvsDialog*);


    // ReCreated
    namespace Remake {
        
    }

    // externs
    //extern ReadFStringType*							ReadFString;
    //extern FNameToWStrType*							FNameToWStr;
    //extern InitializeNameTableType*					InitializeNameTable;
    //extern uint64_t*									ReadFNameToWStrNoIdStart;
    //extern uint64_t*									ReadFNameToWStrWithIdStart;
    //extern uint64_t*									ReadFNameToWStrCommonStart;

    extern GetEndpointKeyValueType*						GetEndpointKeyValue;
    extern SetFStringValueType*							SetFStringValue;
    extern FDateTimeType*								FDateTime;
    extern InitThreadHeaderType*						Init_thread_header;
    extern InitThreadFooterType*						Init_thread_footer;
    extern DialogParamsType*							FMvsDialogParametersConst;
    //extern AddDialogUtilType*							AddDialogUtil;
    //extern AddDialogFrontType*							AddDialogFront;
    extern UFighterGameInstanceConstType*				UFigherGameInstanceConst;
    extern GetFrontendManagerType*						GetFrontendManager;
    extern SingleParamDialogCallbackSetterType*			SingleParamDialogCallbackSetter;
    extern QuitGameParamsType*							QuitGame;
    // Variables
    extern uint64_t*									kSunsetDate;
    extern MVSGame::FText*								GLeave_Lobby_Prompt_Text;

    namespace Instances {
        extern uint64_t* ThisWidget;
        extern UFighterGameInstance* FighterGame;
    }
}