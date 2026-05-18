// ==WindhawkMod==
// @id              taskbar-search-box-position-v1
// @name            Search box on the left
// @description     Moves only the Windows 11 taskbar Search box to the left while keeping app buttons centered
// @version         1.1
// @author          Tanner
// @include         explorer.exe
// @architecture    x86-64
// @compilerOptions -lole32 -loleaut32 -lruntimeobject -lshcore
// ==/WindhawkMod==

// ==WindhawkModReadme==
/*
# Search box on the left

Moves only the Windows 11 taskbar Search box to the left while keeping app
buttons centered.
*/
// ==/WindhawkModReadme==

// ==WindhawkModSettings==
/*
- applyToSecondaryTaskbars: false
  $name: Apply to secondary taskbars
  $description: Move Search on secondary taskbars too
- leftPadding: 0
  $name: Left padding
  $description: Distance, in pixels, from the left edge of the taskbar
- reserveSearchSlot: false
  $name: Reserve original Search slot
  $description: Keep Search's original taskbar slot instead of letting centered icons use that space
*/
// ==/WindhawkModSettings==

#include <windhawk_utils.h>

#include <algorithm>
#include <atomic>
#include <cwchar>
#include <functional>
#include <limits>
#include <mutex>
#include <unordered_map>
#include <unordered_set>

#undef GetCurrentTime

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/base.h>

using namespace winrt::Windows::UI::Xaml;

struct {
    bool applyToSecondaryTaskbars;
    int leftPadding;
    bool reserveSearchSlot;
} g_settings;

std::atomic<bool> g_taskbarViewDllLoaded;
std::atomic<bool> g_unloading;

thread_local bool g_inTaskbarArrangeOverride;

constexpr double kMinimumSearchLayoutSlotWidth = 1.0;

void* CTaskBand_ITaskListWndSite_vftable;
void* CSecondaryTaskBand_ITaskListWndSite_vftable;

using CTaskBand_GetTaskbarHost_t = void*(WINAPI*)(void* pThis, void** result);
CTaskBand_GetTaskbarHost_t CTaskBand_GetTaskbarHost_Original;

void* TaskbarHost_FrameHeight_Original;

using CSecondaryTaskBand_GetTaskbarHost_t = void*(WINAPI*)(void* pThis,
                                                           void** result);
CSecondaryTaskBand_GetTaskbarHost_t CSecondaryTaskBand_GetTaskbarHost_Original;

using std__Ref_count_base__Decref_t = void(WINAPI*)(void* pThis);
std__Ref_count_base__Decref_t std__Ref_count_base__Decref_Original;

std::mutex g_originalMarginsMutex;
std::unordered_map<void*, Thickness> g_originalMargins;

std::mutex g_searchRepeaterItemsMutex;
std::unordered_set<void*> g_searchRepeaterItems;

FrameworkElement EnumChildElements(
    FrameworkElement element,
    std::function<bool(FrameworkElement)> enumCallback) {
    int childrenCount = Media::VisualTreeHelper::GetChildrenCount(element);

    for (int i = 0; i < childrenCount; i++) {
        auto child = Media::VisualTreeHelper::GetChild(element, i)
                         .try_as<FrameworkElement>();
        if (!child) {
            Wh_Log(L"Failed to get child %d of %d", i + 1, childrenCount);
            continue;
        }

        if (enumCallback(child)) {
            return child;
        }
    }

    return nullptr;
}

FrameworkElement FindChildByName(FrameworkElement element, PCWSTR name) {
    return EnumChildElements(element, [name](FrameworkElement child) {
        return child.Name() == name;
    });
}

FrameworkElement FindChildByClassName(FrameworkElement element,
                                      PCWSTR className) {
    return EnumChildElements(element, [className](FrameworkElement child) {
        return winrt::get_class_name(child) == className;
    });
}

FrameworkElement FindDescendantElement(
    FrameworkElement element,
    std::function<bool(FrameworkElement)> enumCallback) {
    int childrenCount = Media::VisualTreeHelper::GetChildrenCount(element);

    for (int i = 0; i < childrenCount; i++) {
        auto child = Media::VisualTreeHelper::GetChild(element, i)
                         .try_as<FrameworkElement>();
        if (!child) {
            continue;
        }

        if (enumCallback(child)) {
            return child;
        }

        if (FrameworkElement descendant =
                FindDescendantElement(child, enumCallback)) {
            return descendant;
        }
    }

    return nullptr;
}

bool HStringContains(winrt::hstring value, PCWSTR needle) {
    return wcsstr(value.c_str(), needle) != nullptr;
}

bool IsTaskbarSearchButton(FrameworkElement element) {
    if (!element) {
        return false;
    }

    auto className = winrt::get_class_name(element);
    auto name = element.Name();
    auto automationId = Automation::AutomationProperties::GetAutomationId(
        element);

    if (className == L"Taskbar.SearchBoxButton" ||
        className == L"Taskbar.SearchBoxLaunchListButton") {
        return automationId.empty() || automationId == L"SearchButton" ||
               HStringContains(automationId, L"Search") ||
               HStringContains(name, L"Search");
    }

    // On some Windows builds/Search modes, the arranged taskbar element is a
    // SearchUx control hosted below the Taskbar search item rather than the
    // Taskbar.SearchBoxButton wrapper used by the XAML resources.
    if (!HStringContains(className, L"Search")) {
        return false;
    }

    return automationId == L"SearchButton" ||
           HStringContains(automationId, L"Search") ||
           HStringContains(name, L"Search");
}

bool HasAncestorNamed(FrameworkElement element, PCWSTR name) {
    DependencyObject parent = Media::VisualTreeHelper::GetParent(element);
    while (parent) {
        auto parentElement = parent.try_as<FrameworkElement>();
        if (parentElement && parentElement.Name() == name) {
            return true;
        }

        parent = Media::VisualTreeHelper::GetParent(parent);
    }

    return false;
}

bool HasParentNamed(FrameworkElement element, PCWSTR name) {
    auto parent = Media::VisualTreeHelper::GetParent(element)
                      .try_as<FrameworkElement>();
    return parent && parent.Name() == name;
}

bool ElementOrDescendantIsTaskbarSearchButton(FrameworkElement element) {
    return IsTaskbarSearchButton(element) ||
           static_cast<bool>(FindDescendantElement(
               element, [](FrameworkElement child) {
                   return IsTaskbarSearchButton(child);
               }));
}

bool ShouldSkipVisualStateGroups(FrameworkElement element) {
    auto className = winrt::get_class_name(element);
    auto parent = Media::VisualTreeHelper::GetParent(element)
                      .try_as<FrameworkElement>();
    if (!parent) {
        return false;
    }

    auto parentClassName = winrt::get_class_name(parent);

    // These Search elements are known to expose a malformed visual state group
    // collection on some Windows builds. Querying the groups can dereference a
    // null entry inside the XAML implementation.
    return (className == L"Taskbar.TaskListButtonPanel" &&
            parentClassName == L"Taskbar.SearchBoxLaunchListButton") ||
           (className == L"SearchUx.SearchUI.SearchButtonRootGrid" &&
            parentClassName == L"SearchUx.SearchUI.SearchPillButton");
}

bool ElementHasActiveSearchVisualState(FrameworkElement element) {
    if (ShouldSkipVisualStateGroups(element)) {
        return false;
    }

    try {
        for (const auto& group :
             VisualStateManager::GetVisualStateGroups(element)) {
            if (!group) {
                continue;
            }

            auto currentState = group.CurrentState();
            if (currentState &&
                HStringContains(currentState.Name(), L"Active")) {
                return true;
            }
        }
    } catch (...) {
        return false;
    }

    return false;
}

bool ElementOrDescendantHasActiveSearchVisualState(FrameworkElement element) {
    return ElementHasActiveSearchVisualState(element) ||
           static_cast<bool>(FindDescendantElement(
               element, [](FrameworkElement child) {
                   return ElementHasActiveSearchVisualState(child);
               }));
}

bool SearchRepeaterItemNeedsReservedSlot(FrameworkElement element) {
    if (winrt::get_class_name(element) ==
        L"Taskbar.SearchBoxLaunchListButton") {
        return true;
    }

    if (FindDescendantElement(element, [](FrameworkElement child) {
            return winrt::get_class_name(child) ==
                   L"SearchUx.SearchUI.SearchPillButton";
        })) {
        return true;
    }

    return ElementOrDescendantHasActiveSearchVisualState(element);
}

void StoreOriginalMarginIfNeeded(FrameworkElement element);
Thickness GetOriginalOrCurrentMargin(FrameworkElement element);

void ApplySearchRepeaterItemMargin(FrameworkElement element,
                                   double widthHint = 0) {
    StoreOriginalMarginIfNeeded(element);

    Thickness margin = GetOriginalOrCurrentMargin(element);
    if (!g_unloading && !g_settings.reserveSearchSlot &&
        !SearchRepeaterItemNeedsReservedSlot(element)) {
        double width = std::max(widthHint, element.ActualWidth());
        if (width > 0) {
            // Don't collapse the repeater item to an exact zero-width slot.
            // The taskbar can re-measure/recycle that item on interaction,
            // making Search disappear. A 1px slot is visually equivalent for
            // centering, but keeps the realized item stable.
            margin.Right = kMinimumSearchLayoutSlotWidth - width;
        }
    }

    element.Margin(margin);
}

void RememberSearchRepeaterItem(FrameworkElement element) {
    void* key = winrt::get_abi(element);
    std::lock_guard<std::mutex> lock(g_searchRepeaterItemsMutex);

    g_searchRepeaterItems.insert(key);
}

bool IsRememberedSearchRepeaterItem(FrameworkElement element) {
    void* key = winrt::get_abi(element);
    std::lock_guard<std::mutex> lock(g_searchRepeaterItemsMutex);

    return g_searchRepeaterItems.find(key) != g_searchRepeaterItems.end();
}

void StoreOriginalMarginIfNeeded(FrameworkElement element) {
    void* key = winrt::get_abi(element);
    std::lock_guard<std::mutex> lock(g_originalMarginsMutex);

    if (g_originalMargins.find(key) == g_originalMargins.end()) {
        g_originalMargins.emplace(key, element.Margin());
    }
}

Thickness GetOriginalOrCurrentMargin(FrameworkElement element) {
    void* key = winrt::get_abi(element);
    std::lock_guard<std::mutex> lock(g_originalMarginsMutex);

    auto it = g_originalMargins.find(key);
    if (it != g_originalMargins.end()) {
        return it->second;
    }

    return element.Margin();
}

bool ApplyStyle(XamlRoot xamlRoot) {
    FrameworkElement xamlRootContent =
        xamlRoot.Content().try_as<FrameworkElement>();

    FrameworkElement taskbarFrameRepeater = nullptr;

    FrameworkElement child = xamlRootContent;
    if (child &&
        (child = FindChildByClassName(child, L"Taskbar.TaskbarFrame")) &&
        (child = FindChildByName(child, L"RootGrid")) &&
        (child = FindChildByName(child, L"TaskbarFrameRepeater"))) {
        taskbarFrameRepeater = child;
    }

    if (!taskbarFrameRepeater) {
        Wh_Log(L"TaskbarFrameRepeater not found");
        return false;
    }

    EnumChildElements(taskbarFrameRepeater, [](FrameworkElement child) {
        auto className = winrt::get_class_name(child);
        auto name = child.Name();
        auto automationId =
            Automation::AutomationProperties::GetAutomationId(child);
        if (HStringContains(className, L"Search") ||
            HStringContains(name, L"Search") ||
            HStringContains(automationId, L"Search")) {
            Wh_Log(L"Search candidate direct child: class=%s name=%s automationId=%s width=%f",
                   className.c_str(), name.c_str(), automationId.c_str(),
                   child.ActualWidth());
        }

        return false;
    });

    auto searchButton =
        EnumChildElements(taskbarFrameRepeater, [](FrameworkElement child) {
            return ElementOrDescendantIsTaskbarSearchButton(child);
        });
    if (!searchButton) {
        Wh_Log(L"SearchBoxButton not found");
        return true;
    }

    Wh_Log(L"Using Search repeater item: class=%s name=%s automationId=%s width=%f",
           winrt::get_class_name(searchButton).c_str(),
           searchButton.Name().c_str(),
           Automation::AutomationProperties::GetAutomationId(searchButton)
               .c_str(),
           searchButton.ActualWidth());

    RememberSearchRepeaterItem(searchButton);
    ApplySearchRepeaterItemMargin(searchButton);

    if (!g_unloading && !g_settings.reserveSearchSlot) {
        searchButton.Dispatcher().TryRunAsync(
            winrt::Windows::UI::Core::CoreDispatcherPriority::High,
            [searchButton]() {
                if (!searchButton) {
                    return;
                }

                double width = searchButton.ActualWidth();
                if (width <= 0) {
                    return;
                }

                double minOtherX = std::numeric_limits<double>::infinity();
                FrameworkElement taskbarFrameRepeater = nullptr;
                DependencyObject parent =
                    Media::VisualTreeHelper::GetParent(searchButton);
                while (parent) {
                    auto parentElement = parent.try_as<FrameworkElement>();
                    if (parentElement &&
                        parentElement.Name() == L"TaskbarFrameRepeater") {
                        taskbarFrameRepeater = parentElement;
                        break;
                    }

                    parent = Media::VisualTreeHelper::GetParent(parent);
                }

                if (!taskbarFrameRepeater) {
                    return;
                }

                EnumChildElements(
                    taskbarFrameRepeater,
                    [searchButton, &minOtherX](FrameworkElement child) {
                        if (child == searchButton) {
                            return false;
                        }

                        auto offset = child.ActualOffset();
                        if (offset.x >= 0 && offset.x < minOtherX) {
                            minOtherX = offset.x;
                        }

                        return false;
                    });

                if (minOtherX < width + g_settings.leftPadding) {
                    Wh_Log(L"Search overlaps centered items, reserving original Search slot");
                    Thickness margin = GetOriginalOrCurrentMargin(searchButton);
                    searchButton.Margin(margin);
                }
            });
    }

    return true;
}

XamlRoot XamlRootFromTaskbarHostSharedPtr(void* taskbarHostSharedPtr[2]) {
    if (!taskbarHostSharedPtr[0] && !taskbarHostSharedPtr[1]) {
        return nullptr;
    }

    size_t taskbarElementIUnknownOffset = 0x48;

#if defined(_M_X64)
    {
        const BYTE* b = (const BYTE*)TaskbarHost_FrameHeight_Original;
        if (b[0] == 0x48 && b[1] == 0x83 && b[2] == 0xEC && b[4] == 0x48 &&
            b[5] == 0x83 && b[6] == 0xC1 && b[7] <= 0x7F) {
            taskbarElementIUnknownOffset = b[7];
        } else {
            Wh_Log(L"Unsupported TaskbarHost::FrameHeight");
        }
    }
#else
#error "Unsupported architecture"
#endif

    auto* taskbarElementIUnknown =
        *(IUnknown**)((BYTE*)taskbarHostSharedPtr[0] +
                      taskbarElementIUnknownOffset);

    FrameworkElement taskbarElement = nullptr;
    taskbarElementIUnknown->QueryInterface(winrt::guid_of<FrameworkElement>(),
                                           winrt::put_abi(taskbarElement));

    auto result = taskbarElement ? taskbarElement.XamlRoot() : nullptr;

    std__Ref_count_base__Decref_Original(taskbarHostSharedPtr[1]);

    return result;
}

XamlRoot GetTaskbarXamlRoot(HWND hTaskbarWnd) {
    HWND hTaskSwWnd = (HWND)GetProp(hTaskbarWnd, L"TaskbandHWND");
    if (!hTaskSwWnd) {
        return nullptr;
    }

    void* taskBand = (void*)GetWindowLongPtr(hTaskSwWnd, 0);
    void* taskBandForTaskListWndSite = taskBand;
    for (int i = 0; *(void**)taskBandForTaskListWndSite !=
                    CTaskBand_ITaskListWndSite_vftable;
         i++) {
        if (i == 20) {
            return nullptr;
        }

        taskBandForTaskListWndSite = (void**)taskBandForTaskListWndSite + 1;
    }

    void* taskbarHostSharedPtr[2]{};
    CTaskBand_GetTaskbarHost_Original(taskBandForTaskListWndSite,
                                      taskbarHostSharedPtr);

    return XamlRootFromTaskbarHostSharedPtr(taskbarHostSharedPtr);
}

XamlRoot GetSecondaryTaskbarXamlRoot(HWND hSecondaryTaskbarWnd) {
    HWND hTaskSwWnd =
        (HWND)FindWindowEx(hSecondaryTaskbarWnd, nullptr, L"WorkerW", nullptr);
    if (!hTaskSwWnd) {
        return nullptr;
    }

    void* taskBand = (void*)GetWindowLongPtr(hTaskSwWnd, 0);
    void* taskBandForTaskListWndSite = taskBand;
    for (int i = 0; *(void**)taskBandForTaskListWndSite !=
                    CSecondaryTaskBand_ITaskListWndSite_vftable;
         i++) {
        if (i == 20) {
            return nullptr;
        }

        taskBandForTaskListWndSite = (void**)taskBandForTaskListWndSite + 1;
    }

    void* taskbarHostSharedPtr[2]{};
    CSecondaryTaskBand_GetTaskbarHost_Original(taskBandForTaskListWndSite,
                                               taskbarHostSharedPtr);

    return XamlRootFromTaskbarHostSharedPtr(taskbarHostSharedPtr);
}

using RunFromWindowThreadProc_t = void(WINAPI*)(void* parameter);

bool RunFromWindowThread(HWND hWnd,
                         RunFromWindowThreadProc_t proc,
                         void* procParam) {
    static const UINT runFromWindowThreadRegisteredMsg =
        RegisterWindowMessage(L"Windhawk_RunFromWindowThread_" WH_MOD_ID);

    struct RUN_FROM_WINDOW_THREAD_PARAM {
        RunFromWindowThreadProc_t proc;
        void* procParam;
    };

    DWORD dwThreadId = GetWindowThreadProcessId(hWnd, nullptr);
    if (dwThreadId == 0) {
        return false;
    }

    if (dwThreadId == GetCurrentThreadId()) {
        proc(procParam);
        return true;
    }

    HHOOK hook = SetWindowsHookEx(
        WH_CALLWNDPROC,
        [](int nCode, WPARAM wParam, LPARAM lParam) -> LRESULT {
            if (nCode == HC_ACTION) {
                const CWPSTRUCT* cwp = (const CWPSTRUCT*)lParam;
                if (cwp->message == runFromWindowThreadRegisteredMsg) {
                    auto* param =
                        (RUN_FROM_WINDOW_THREAD_PARAM*)cwp->lParam;
                    param->proc(param->procParam);
                }
            }

            return CallNextHookEx(nullptr, nCode, wParam, lParam);
        },
        nullptr, dwThreadId);
    if (!hook) {
        return false;
    }

    RUN_FROM_WINDOW_THREAD_PARAM param;
    param.proc = proc;
    param.procParam = procParam;
    SendMessage(hWnd, runFromWindowThreadRegisteredMsg, 0, (LPARAM)&param);

    UnhookWindowsHookEx(hook);

    return true;
}

void ApplySettingsFromTaskbarThread() {
    Wh_Log(L"Applying settings");

    EnumThreadWindows(
        GetCurrentThreadId(),
        [](HWND hWnd, LPARAM) -> BOOL {
            WCHAR szClassName[32];
            if (GetClassName(hWnd, szClassName, ARRAYSIZE(szClassName)) == 0) {
                return TRUE;
            }

            XamlRoot xamlRoot = nullptr;
            if (_wcsicmp(szClassName, L"Shell_TrayWnd") == 0) {
                xamlRoot = GetTaskbarXamlRoot(hWnd);
            } else if (g_settings.applyToSecondaryTaskbars &&
                       _wcsicmp(szClassName, L"Shell_SecondaryTrayWnd") == 0) {
                xamlRoot = GetSecondaryTaskbarXamlRoot(hWnd);
            } else {
                return TRUE;
            }

            if (!xamlRoot) {
                Wh_Log(L"Getting XamlRoot failed");
                return TRUE;
            }

            ApplyStyle(xamlRoot);
            return TRUE;
        },
        0);
}

void ApplySettings(HWND hTaskbarWnd) {
    RunFromWindowThread(
        hTaskbarWnd, [](void*) { ApplySettingsFromTaskbarThread(); }, nullptr);
}

HWND FindCurrentProcessTaskbarWnd() {
    HWND hTaskbarWnd = nullptr;

    EnumWindows(
        [](HWND hWnd, LPARAM lParam) -> BOOL {
            DWORD dwProcessId;
            WCHAR className[32];
            if (GetWindowThreadProcessId(hWnd, &dwProcessId) &&
                dwProcessId == GetCurrentProcessId() &&
                GetClassName(hWnd, className, ARRAYSIZE(className)) &&
                _wcsicmp(className, L"Shell_TrayWnd") == 0) {
                *reinterpret_cast<HWND*>(lParam) = hWnd;
                return FALSE;
            }

            return TRUE;
        },
        reinterpret_cast<LPARAM>(&hTaskbarWnd));

    return hTaskbarWnd;
}

using IUIElement_Arrange_t =
    HRESULT(WINAPI*)(void* pThis, winrt::Windows::Foundation::Rect rect);
IUIElement_Arrange_t IUIElement_Arrange_Original;

HRESULT WINAPI IUIElement_Arrange_Hook(void* pThis,
                                       winrt::Windows::Foundation::Rect rect) {
    auto original = [=] { return IUIElement_Arrange_Original(pThis, rect); };

    if (!g_inTaskbarArrangeOverride || g_unloading) {
        return original();
    }

    FrameworkElement element = nullptr;
    ((IUnknown*)pThis)
        ->QueryInterface(winrt::guid_of<FrameworkElement>(),
                         winrt::put_abi(element));
    if (!element || !HasParentNamed(element, L"TaskbarFrameRepeater")) {
        return original();
    }

    bool isSearchRepeaterItem = IsRememberedSearchRepeaterItem(element);
    if (!isSearchRepeaterItem &&
        ElementOrDescendantIsTaskbarSearchButton(element)) {
        RememberSearchRepeaterItem(element);
        StoreOriginalMarginIfNeeded(element);
        isSearchRepeaterItem = true;
    }

    if (!isSearchRepeaterItem) {
        return original();
    }

    ApplySearchRepeaterItemMargin(element, rect.Width);

    static bool loggedArrange;
    if (!loggedArrange) {
        loggedArrange = true;
        Wh_Log(L"Arranging Search element left: class=%s name=%s automationId=%s originalX=%f width=%f",
               winrt::get_class_name(element).c_str(), element.Name().c_str(),
               Automation::AutomationProperties::GetAutomationId(element)
                   .c_str(),
               rect.X, rect.Width);
    }

    winrt::Windows::Foundation::Rect newRect = rect;
    newRect.X = g_settings.leftPadding;
    return IUIElement_Arrange_Original(pThis, newRect);
}

using TaskbarCollapsibleLayoutXamlTraits_ArrangeOverride_t =
    HRESULT(WINAPI*)(void* pThis,
                     void* context,
                     winrt::Windows::Foundation::Size size,
                     winrt::Windows::Foundation::Size* resultSize);
TaskbarCollapsibleLayoutXamlTraits_ArrangeOverride_t
    TaskbarCollapsibleLayoutXamlTraits_ArrangeOverride_Original;

HRESULT WINAPI TaskbarCollapsibleLayoutXamlTraits_ArrangeOverride_Hook(
    void* pThis,
    void* context,
    winrt::Windows::Foundation::Size size,
    winrt::Windows::Foundation::Size* resultSize) {
    [[maybe_unused]] static bool hooked = [] {
        Shapes::Rectangle rectangle;
        IUIElement element = rectangle;

        void** vtable = *(void***)winrt::get_abi(element);
        auto arrange = (IUIElement_Arrange_t)vtable[92];

        WindhawkUtils::SetFunctionHook(arrange, IUIElement_Arrange_Hook,
                                       &IUIElement_Arrange_Original);
        Wh_ApplyHookOperations();
        return true;
    }();

    bool wasInTaskbarArrangeOverride = g_inTaskbarArrangeOverride;
    g_inTaskbarArrangeOverride = true;

    HRESULT ret = TaskbarCollapsibleLayoutXamlTraits_ArrangeOverride_Original(
        pThis, context, size, resultSize);

    g_inTaskbarArrangeOverride = wasInTaskbarArrangeOverride;

    return ret;
}

bool HookTaskbarDllSymbols() {
    HMODULE module =
        LoadLibraryEx(L"taskbar.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) {
        Wh_Log(L"Failed to load taskbar.dll");
        return false;
    }

    WindhawkUtils::SYMBOL_HOOK taskbarDllHooks[] = {
        {
            {LR"(const CTaskBand::`vftable'{for `ITaskListWndSite'})"},
            &CTaskBand_ITaskListWndSite_vftable,
        },
        {
            {LR"(const CSecondaryTaskBand::`vftable'{for `ITaskListWndSite'})"},
            &CSecondaryTaskBand_ITaskListWndSite_vftable,
        },
        {
            {LR"(public: virtual class std::shared_ptr<class TaskbarHost> __cdecl CTaskBand::GetTaskbarHost(void)const )"},
            &CTaskBand_GetTaskbarHost_Original,
        },
        {
            {LR"(public: int __cdecl TaskbarHost::FrameHeight(void)const )"},
            &TaskbarHost_FrameHeight_Original,
        },
        {
            {LR"(public: virtual class std::shared_ptr<class TaskbarHost> __cdecl CSecondaryTaskBand::GetTaskbarHost(void)const )"},
            &CSecondaryTaskBand_GetTaskbarHost_Original,
        },
        {
            {LR"(public: void __cdecl std::_Ref_count_base::_Decref(void))"},
            &std__Ref_count_base__Decref_Original,
        },
    };

    return HookSymbols(module, taskbarDllHooks, ARRAYSIZE(taskbarDllHooks));
}

bool HookTaskbarViewDllSymbols(HMODULE module) {
    WindhawkUtils::SYMBOL_HOOK symbolHooks[] = {
        {
            {LR"(public: virtual int __cdecl winrt::impl::produce<struct winrt::Taskbar::implementation::TaskbarCollapsibleLayout,struct winrt::Microsoft::UI::Xaml::Controls::IVirtualizingLayoutOverrides>::ArrangeOverride(void *,struct winrt::Windows::Foundation::Size,struct winrt::Windows::Foundation::Size *))"},
            &TaskbarCollapsibleLayoutXamlTraits_ArrangeOverride_Original,
            TaskbarCollapsibleLayoutXamlTraits_ArrangeOverride_Hook,
        },
    };

    return HookSymbols(module, symbolHooks, ARRAYSIZE(symbolHooks));
}

HMODULE GetTaskbarViewModuleHandle() {
    HMODULE module = GetModuleHandle(L"Taskbar.View.dll");
    if (!module) {
        module = GetModuleHandle(L"ExplorerExtensions.dll");
    }

    return module;
}

void HandleLoadedModuleIfTaskbarView(HMODULE module, LPCWSTR lpLibFileName) {
    if (!g_taskbarViewDllLoaded && GetTaskbarViewModuleHandle() == module &&
        !g_taskbarViewDllLoaded.exchange(true)) {
        Wh_Log(L"Loaded %s", lpLibFileName);

        if (HookTaskbarViewDllSymbols(module)) {
            Wh_ApplyHookOperations();
        }
    }
}

using LoadLibraryExW_t = decltype(&LoadLibraryExW);
LoadLibraryExW_t LoadLibraryExW_Original;
HMODULE WINAPI LoadLibraryExW_Hook(LPCWSTR lpLibFileName,
                                   HANDLE hFile,
                                   DWORD dwFlags) {
    HMODULE module = LoadLibraryExW_Original(lpLibFileName, hFile, dwFlags);
    if (module) {
        HandleLoadedModuleIfTaskbarView(module, lpLibFileName);
    }

    return module;
}

void LoadSettings() {
    g_settings.applyToSecondaryTaskbars =
        Wh_GetIntSetting(L"applyToSecondaryTaskbars");
    g_settings.leftPadding = Wh_GetIntSetting(L"leftPadding");
    g_settings.reserveSearchSlot = Wh_GetIntSetting(L"reserveSearchSlot");
}

BOOL Wh_ModInit() {
    Wh_Log(L">");

    LoadSettings();

    if (!HookTaskbarDllSymbols()) {
        return FALSE;
    }

    if (HMODULE taskbarViewModule = GetTaskbarViewModuleHandle()) {
        g_taskbarViewDllLoaded = true;
        if (!HookTaskbarViewDllSymbols(taskbarViewModule)) {
            return FALSE;
        }
    } else {
        Wh_Log(L"Taskbar view module not loaded yet");

        HMODULE kernelBaseModule = GetModuleHandle(L"kernelbase.dll");
        auto pKernelBaseLoadLibraryExW =
            (decltype(&LoadLibraryExW))GetProcAddress(kernelBaseModule,
                                                      "LoadLibraryExW");
        WindhawkUtils::SetFunctionHook(pKernelBaseLoadLibraryExW,
                                       LoadLibraryExW_Hook,
                                       &LoadLibraryExW_Original);
    }

    return TRUE;
}

void Wh_ModAfterInit() {
    Wh_Log(L">");

    if (!g_taskbarViewDllLoaded) {
        if (HMODULE taskbarViewModule = GetTaskbarViewModuleHandle()) {
            if (!g_taskbarViewDllLoaded.exchange(true)) {
                Wh_Log(L"Got Taskbar.View.dll");

                if (HookTaskbarViewDllSymbols(taskbarViewModule)) {
                    Wh_ApplyHookOperations();
                }
            }
        }
    }

    HWND hTaskbarWnd = FindCurrentProcessTaskbarWnd();
    if (hTaskbarWnd) {
        ApplySettings(hTaskbarWnd);
    }
}

void Wh_ModBeforeUninit() {
    Wh_Log(L">");

    g_unloading = true;

    HWND hTaskbarWnd = FindCurrentProcessTaskbarWnd();
    if (hTaskbarWnd) {
        ApplySettings(hTaskbarWnd);
    }
}

BOOL Wh_ModSettingsChanged(BOOL*) {
    Wh_Log(L">");

    LoadSettings();

    HWND hTaskbarWnd = FindCurrentProcessTaskbarWnd();
    if (hTaskbarWnd) {
        ApplySettings(hTaskbarWnd);
    }

    return TRUE;
}
