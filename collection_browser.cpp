#include "stdafx.h"
#include <foobar2000/SDK/coreDarkMode.h>

namespace lxmusic {
namespace {

struct browser_frame {
    std::string title;
    std::vector<std::string> items;
    SearchPage page;
    bool search = false;
    int focus = 0, top = 0;
    std::vector<int> selected{ 0 };
};

struct browser_history {
    std::vector<browser_frame> frames;
    std::shared_ptr<NavigationOrigin> origin;
};

class collection_browser;
struct browser_session {
    std::weak_ptr<collection_browser> window;
};
std::vector<std::shared_ptr<collection_browser>> g_browsers;
std::shared_ptr<collection_browser> open_browser(browser_history history,
    std::shared_ptr<browser_session> session = std::make_shared<browser_session>());

class collection_browser : public CDialogImpl<collection_browser>, public message_filter_impl_base,
    public std::enable_shared_from_this<collection_browser> {
public:
    enum { IDD = IDD_LX_COLLECTIONS };
    collection_browser(browser_history history, std::shared_ptr<browser_session> session)
        : m_history(std::move(history)), m_session(std::move(session)) {}

    BEGIN_MSG_MAP(collection_browser)
        MSG_WM_INITDIALOG(OnInitDialog)
        MSG_WM_DESTROY(OnDestroy)
        MSG_WM_SIZE(OnSize)
        MESSAGE_HANDLER(WM_ACTIVATE, OnActivate)
        MESSAGE_HANDLER(WM_CONTEXTMENU, OnContextMenu)
        MESSAGE_HANDLER(WM_GETMINMAXINFO, OnMinMax)
        NOTIFY_HANDLER(IDC_LX_COLLECTION_LIST, NM_DBLCLK, OnDoubleClick)
        COMMAND_ID_HANDLER(IDOK, OnOpen)
        COMMAND_ID_HANDLER(IDCANCEL, OnClose)
        COMMAND_ID_HANDLER(IDC_LX_COLLECTION_BACK, OnBack)
    END_MSG_MAP()

    bool Active() const {
        const auto focus = ::GetFocus();
        return m_hWnd && ::IsWindowVisible(m_hWnd) && ::IsWindowEnabled(m_hWnd) &&
            (m_dispatching_shortcut || (focus && (focus == m_hWnd || ::IsChild(m_hWnd, focus))));
    }

    void RestoreFocus() {
        if (m_focus_pending || !IsWindow()) return;
        m_focus_pending = true;
        const std::weak_ptr<collection_browser> weak = shared_from_this();
        fb2k::inMainThread([weak] {
            const auto self = weak.lock();
            if (!self) return;
            self->m_focus_pending = false;
            if (core_api::is_shutting_down() || !can_restore_browser_focus(self->m_hWnd)) return;
            ::SetForegroundWindow(self->m_hWnd);
            ::SetFocus(::IsWindowEnabled(self->List()) ? self->List() : self->GetDlgItem(IDCANCEL).m_hWnd);
        });
    }

    void RestoreHistory(const browser_history& history) {
        if (m_abort) { m_abort->abort(); m_abort.reset(); }
        m_history = history;
        Busy(false);
        Render();
    }

    bool pretranslate_message(MSG* message) override {
        if (!Active() || (message->hwnd != m_hWnd && !::IsChild(m_hWnd, message->hwnd))) return false;
        // Keep the object alive if a shortcut opens songs and destroys this window.
        const auto lifetime = shared_from_this();
        if (message->message == WM_KEYUP &&
            (message->wParam == VK_APPS || message->wParam == VK_MULTIPLY)) return true;
        if (message->message == WM_KEYDOWN && ::GetFocus() == List()) {
            const auto key = message->wParam;
            const bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            if (key == VK_APPS || key == VK_MULTIPLY || (key == VK_F10 && shift)) {
                ContextMenu(POINT{ -1, -1 });
                return true;
            }
        }
        try {
            auto shortcuts = keyboard_shortcut_manager_v2::get();
            {
                // Host shortcut dispatch can temporarily activate the main window.
                // Navigation commands must still target the browser that received the key.
                const pfc::booltoggle dispatch(m_dispatching_shortcut, true);
                if (shortcuts->pretranslate_message(message, m_hWnd)) return true;
            }
            if (message->message == WM_KEYDOWN && message->wParam == VK_RETURN && ::GetFocus() == List()) {
                Open();
                return true;
            }
            if (message->message == WM_KEYDOWN && message->wParam == VK_BACK &&
                (message->lParam & (1LL << 30)) == 0) {
                for (const auto key : { VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN })
                    if (GetKeyState(key) & 0x8000) return false;
                pfc::string8 assigned;
                if (!shortcuts->get_key_description_for_action(guid_back, pfc::guid_null, assigned,
                        keyboard_shortcut_manager::TYPE_MAIN, false) &&
                    !shortcuts->get_key_description_for_action(guid_back, pfc::guid_null, assigned,
                        keyboard_shortcut_manager::TYPE_MAIN, true)) {
                    Back();
                    return true;
                }
            }
        } catch (...) {}
        return false;
    }

    void Back() {
        if (m_abort) return;
        if (m_history.frames.size() > 1) {
            m_history.frames.pop_back();
            Render();
        } else if (m_history.origin && restore_navigation_origin(m_history.origin)) DestroyWindow();
        else { Status("已经是第一层"); RestoreFocus(); }
    }

    void PageAction(const std::string& action) {
        if (m_abort) return;
        if (!Current().search) { Status("当前列表没有翻页操作"); return; }
        const auto page = Current().page;
        auto id = action;
        if (action == "previous") {
            if (page.page <= 1) { Status("已经是第一页"); return; }
            id = std::to_string(page.page - 1);
        } else if (action == "next") {
            if (!page.has_next) { Status("已经是最后一页"); return; }
            id = std::to_string(page.page + 1);
        }
        for (const auto& encoded : Current().items) {
            const auto payload = decode_payload(encoded);
            if (payload.is_navigation() && payload.get(pf_id) == id) { Navigate(payload); return; }
        }
        Status("当前页没有这个翻页操作");
    }

private:
    browser_history m_history;
    std::shared_ptr<browser_session> m_session;
    std::shared_ptr<abort_callback_impl> m_abort;
    bool m_focus_pending = false, m_dispatching_shortcut = false;
    fb2k::CCoreDarkModeHooks m_dark;

    HWND List() const { return ::GetDlgItem(m_hWnd, IDC_LX_COLLECTION_LIST); }
    browser_frame& Current() { return m_history.frames.back(); }
    void Status(const std::string& text) { uSetDlgItemText(m_hWnd, IDC_LX_STATUS, text.c_str()); }

    DialogOrigin DialogContext() {
        // The session survives entering songs and recreating the browser on Back.
        // A pending download must follow that replacement without reopening a
        // browser that the user explicitly closed.
        const auto session = m_session;
        return {
            [session]() -> HWND {
                const auto self = session->window.lock();
                if (!self || !self->IsWindow() || !self->IsWindowVisible()) return nullptr;
                // A second download can finish while a modal dialog is open.
                const auto popup = ::GetLastActivePopup(self->m_hWnd);
                return popup && ::IsWindowVisible(popup) ? popup : self->m_hWnd;
            },
            [session] { if (const auto self = session->window.lock()) self->RestoreFocus(); }
        };
    }

    LRESULT OnActivate(UINT, WPARAM state, LPARAM, BOOL& handled) {
        handled = FALSE;
        if (LOWORD(state) != WA_INACTIVE) RestoreFocus();
        return 0;
    }

    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        modeless_dialog_manager::g_add(m_hWnd);
        m_dark.AddDialogWithControls(m_hWnd);
        ListView_SetExtendedListViewStyleEx(List(), LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER,
            LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER);
        LVCOLUMNW column{};
        column.mask = LVCF_TEXT | LVCF_WIDTH;
        column.cx = 600;
        column.pszText = const_cast<wchar_t*>(L"名称");
        ListView_InsertColumn(List(), 0, &column);
        Render();
        RECT client{};
        GetClientRect(&client);
        OnSize(0, CSize(client.right, client.bottom));
        return FALSE;
    }

    void OnDestroy() {
        if (m_abort) m_abort->abort();
        modeless_dialog_manager::g_remove(m_hWnd);
        if (!core_api::is_shutting_down()) {
            const auto self = shared_from_this();
            fb2k::inMainThread([self] {
                g_browsers.erase(std::remove(g_browsers.begin(), g_browsers.end(), self), g_browsers.end());
            });
        }
    }

    void OnSize(UINT, CSize size) {
        if (!List()) return;
        RECT metrics{ 0, 0, 8, 16 };
        MapDialogRect(&metrics);
        const int margin = metrics.right, button_height = metrics.bottom;
        const int width = size.cx, height = size.cy;
        const int bottom = std::max(margin, height - margin - button_height);
        const int status = std::max(margin, bottom - margin - button_height);
        ::SetWindowPos(List(), nullptr, margin, margin, std::max(30, width - margin * 2),
            std::max(30, status - margin * 2), SWP_NOZORDER);
        GetDlgItem(IDC_LX_STATUS).SetWindowPos(nullptr, margin, status, std::max(30, width - margin * 2),
            button_height, SWP_NOZORDER);
        GetDlgItem(IDC_LX_COLLECTION_BACK).SetWindowPos(nullptr, margin, bottom, margin * 10, button_height, SWP_NOZORDER);
        GetDlgItem(IDOK).SetWindowPos(nullptr, width - margin * 16, bottom, margin * 7, button_height, SWP_NOZORDER);
        GetDlgItem(IDCANCEL).SetWindowPos(nullptr, width - margin * 8, bottom, margin * 7, button_height, SWP_NOZORDER);
        ListView_SetColumnWidth(List(), 0, std::max(20, width - margin * 2 - GetSystemMetrics(SM_CXVSCROLL) - 4));
    }

    LRESULT OnMinMax(UINT, WPARAM, LPARAM value, BOOL&) {
        RECT minimum{ 0, 0, 310, 180 };
        MapDialogRect(&minimum);
        auto info = reinterpret_cast<MINMAXINFO*>(value);
        info->ptMinTrackSize = POINT{ minimum.right, minimum.bottom };
        return 0;
    }

    void SavePosition() {
        auto& frame = Current();
        frame.focus = ListView_GetNextItem(List(), -1, LVNI_FOCUSED);
        frame.top = ListView_GetTopIndex(List());
        frame.selected.clear();
        for (int row = -1; (row = ListView_GetNextItem(List(), row, LVNI_SELECTED)) >= 0; ) frame.selected.push_back(row);
    }

    void Render() {
        auto& frame = Current();
        uSetWindowText(m_hWnd, frame.title.c_str());
        ::SendMessage(List(), WM_SETREDRAW, FALSE, 0);
        ListView_DeleteAllItems(List());
        for (size_t i = 0; i < frame.items.size(); ++i) {
            const auto text = collection_result_label(decode_payload(frame.items[i]));
            pfc::stringcvt::string_wide_from_utf8 label(text.c_str());
            LVITEMW item{};
            item.mask = LVIF_TEXT;
            item.iItem = static_cast<int>(i);
            item.pszText = const_cast<wchar_t*>(label.get_ptr());
            ListView_InsertItem(List(), &item);
        }
        for (const auto selected : frame.selected)
            if (selected >= 0 && static_cast<size_t>(selected) < frame.items.size())
                ListView_SetItemState(List(), selected, LVIS_SELECTED, LVIS_SELECTED);
        if (frame.focus >= 0 && static_cast<size_t>(frame.focus) < frame.items.size()) {
            ListView_SetItemState(List(), frame.focus, LVIS_FOCUSED, LVIS_FOCUSED);
            ListView_EnsureVisible(List(), frame.focus, FALSE);
        }
        if (frame.top > 0 && static_cast<size_t>(frame.top) < frame.items.size()) {
            RECT row{};
            if (ListView_GetItemRect(List(), 0, &row, LVIR_BOUNDS))
                ListView_Scroll(List(), 0, (frame.top - ListView_GetTopIndex(List())) * (row.bottom - row.top));
        }
        ::SendMessage(List(), WM_SETREDRAW, TRUE, 0);
        ::InvalidateRect(List(), nullptr, TRUE);
        GetDlgItem(IDC_LX_COLLECTION_BACK).EnableWindow(m_history.frames.size() > 1 || m_history.origin);
        Status(frame.search && (frame.page.total_pages != 1 || frame.page.has_next)
            ? search_page_label(frame.page) : "回车打开，右键下载，退格返回上一层");
        RestoreFocus();
    }

    void Busy(bool busy) {
        GetDlgItem(IDOK).EnableWindow(!busy);
        GetDlgItem(IDC_LX_COLLECTION_LIST).EnableWindow(!busy);
        GetDlgItem(IDC_LX_COLLECTION_BACK).EnableWindow(!busy && (m_history.frames.size() > 1 || m_history.origin));
    }

    void Fetch(std::function<browser_frame(abort_callback&)> worker, bool replace) {
        if (m_abort) return;
        SavePosition();
        const auto abort = std::make_shared<abort_callback_impl>();
        m_abort = abort;
        Busy(true);
        Status(replace ? "正在加载这一页…" : "正在打开…");
        const std::weak_ptr<collection_browser> weak = shared_from_this();
        fb2k::splitTask([weak, abort, worker = std::move(worker), replace] {
            browser_frame result;
            std::string error;
            try { result = worker(*abort); }
            catch (const exception_aborted&) { return; }
            catch (const std::exception& e) { error = e.what(); }
            catch (...) { error = "无法读取在线结果"; }
            if (abort->is_aborting()) return;
            fb2k::inMainThread([weak, abort, replace, result = std::move(result), error] () mutable {
                const auto self = weak.lock();
                if (!self || !self->IsWindow() || abort->is_aborting() || self->m_abort != abort) return;
                self->m_abort.reset();
                self->Busy(false);
                try {
                    if (!error.empty()) throw std::runtime_error(error);
                    if (result.items.empty()) throw std::runtime_error("没有返回可用结果，原列表已保留");
                    if (has_collection_results(result.items)) {
                        if (replace) self->Current() = std::move(result);
                        else self->m_history.frames.push_back(std::move(result));
                        self->Render();
                    } else {
                        auto snapshot = self->m_history;
                        const auto origin = capture_browser_navigation([snapshot, session = self->m_session] {
                            // Repeated return commands reuse the same live browser.
                            const auto browser = session->window.lock();
                            if (browser && browser->IsWindow()) browser->RestoreHistory(snapshot);
                            else open_browser(snapshot, session);
                            return true;
                        });
                        add_expanded_payloads(result.items, result.title, origin);
                        self->DestroyWindow();
                        ui_control::get()->activate();
                    }
                } catch (const std::exception& e) {
                    self->Status(e.what());
                    self->GetDlgItem(IDC_LX_COLLECTION_LIST).SetFocus();
                    self->MessageBoxW(pfc::stringcvt::string_wide_from_utf8(e.what()), L"LX 在线音乐", MB_OK | MB_ICONERROR);
                    self->RestoreFocus();
                }
            });
        });
    }

    void Navigate(const Payload& payload) {
        const auto previous = decode_search_state(payload.get(pf_music_info_json));
        int requested = previous.page;
        const auto action = payload.get(pf_id);
        const bool last = action == "last";
        if (last) requested = previous.total_pages;
        else if (action == "jump") {
            const bool accepted = show_page_dialog(previous, requested, m_hWnd);
            RestoreFocus();
            if (!accepted) return;
        }
        else if (!parse_search_page_number(action, previous.total_pages, requested)) return;
        if (requested < 1 || requested == previous.page) return;
        const auto title = Current().title;
        Fetch([previous, requested, last, title](abort_callback& abort) {
            browser_frame result;
            result.title = title; result.search = true;
            fetch_browser_search_page(result.items, result.page, previous, requested, last, abort);
            return result;
        }, true);
    }

    void Open() {
        if (m_abort) return;
        int row = ListView_GetNextItem(List(), -1, LVNI_FOCUSED);
        if (row < 0) row = ListView_GetNextItem(List(), -1, LVNI_SELECTED);
        if (row < 0 || static_cast<size_t>(row) >= Current().items.size()) return;
        const auto encoded = Current().items[row];
        const auto payload = decode_payload(encoded);
        if (payload.is_navigation()) { Navigate(payload); return; }
        if (!payload.is_collection()) return;
        const auto title = "LX " + payload.get(pf_title);
        const auto quality = payload.get(pf_preferred_quality).empty() ? "flac" : payload.get(pf_preferred_quality);
        Fetch([encoded, title, quality](abort_callback& abort) {
            browser_frame result;
            result.title = title;
            result.items = bridge_items(run_bridge({ "expand", encoded, quality }, abort));
            return result;
        }, false);
    }

    LRESULT OnOpen(WORD, WORD, HWND, BOOL&) { Open(); return 0; }
    LRESULT OnBack(WORD, WORD, HWND, BOOL&) { Back(); return 0; }
    LRESULT OnClose(WORD, WORD, HWND, BOOL&) { DestroyWindow(); return 0; }
    LRESULT OnDoubleClick(int, LPNMHDR header, BOOL&) {
        if (reinterpret_cast<NMITEMACTIVATE*>(header)->iItem >= 0) Open();
        return 0;
    }
    LRESULT OnContextMenu(UINT, WPARAM, LPARAM location, BOOL&) {
        ContextMenu(POINT{ GET_X_LPARAM(location), GET_Y_LPARAM(location) });
        return 0;
    }

    void ContextMenu(POINT point) {
        if (m_abort) return;
        const auto lifetime = shared_from_this();
        if (point.x == -1 && point.y == -1) {
            int row = ListView_GetNextItem(List(), -1, LVNI_FOCUSED);
            if (row < 0) return;
            RECT bounds{};
            ListView_GetItemRect(List(), row, &bounds, LVIR_BOUNDS);
            point = POINT{ bounds.left, bounds.bottom };
            ::ClientToScreen(List(), &point);
        } else {
            LVHITTESTINFO hit{}; hit.pt = point;
            ::ScreenToClient(List(), &hit.pt);
            const int row = ListView_HitTest(List(), &hit);
            if (row < 0) return;
            if (!(ListView_GetItemState(List(), row, LVIS_SELECTED) & LVIS_SELECTED))
                ListView_SetItemState(List(), -1, 0, LVIS_SELECTED);
            ListView_SetItemState(List(), -1, 0, LVIS_FOCUSED);
            ListView_SetItemState(List(), row, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
        }
        metadb_handle_list handles;
        bool downloadable = false;
        for (int row = -1; (row = ListView_GetNextItem(List(), row, LVNI_SELECTED)) >= 0; ) {
            const auto& encoded = Current().items[row];
            const auto payload = decode_payload(encoded);
            if (payload.get(pf_kind) == "album" || is_ranking_payload(payload)) downloadable = true;
            auto handle = metadb::get()->handle_create(payload_to_uri(encoded).c_str(), 0);
            hint_payload_info(handle, payload);
            handles.add_item(handle);
        }
        const bool all_albums = Current().search && Current().page.options.type == "album";
        HMENU menu = CreatePopupMenu();
        if (!menu) return;
        AppendMenuW(menu, MF_STRING, 1, L"打开");
        if (downloadable || all_albums) AppendMenuW(menu, MF_STRING, 2, L"批量下载…");
        SetForegroundWindow(m_hWnd);
        const auto action = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
            point.x, point.y, 0, m_hWnd, nullptr);
        DestroyMenu(menu);
        if (action == 1) Open();
        else if (action == 2) command_download(handles, Current().search ? &Current().page : nullptr, DialogContext());
        else RestoreFocus();
    }
};

std::shared_ptr<collection_browser> open_browser(browser_history history, std::shared_ptr<browser_session> session) {
    auto browser = std::make_shared<collection_browser>(std::move(history), session);
    g_browsers.push_back(browser);
    if (!browser->Create(core_api::get_main_window())) {
        g_browsers.erase(std::remove(g_browsers.begin(), g_browsers.end(), browser), g_browsers.end());
        throw std::runtime_error("无法打开 LX 浏览列表");
    }
    session->window = browser;
    browser->ShowWindow(SW_SHOWNOACTIVATE);
    browser->RestoreFocus();
    return browser;
}

class browser_cleanup : public initquit {
public:
    void on_quit() override {
        auto browsers = std::move(g_browsers);
        for (const auto& browser : browsers) if (browser->IsWindow()) browser->DestroyWindow();
    }
};
static initquit_factory_t<browser_cleanup> g_browser_cleanup;

} // namespace

void show_collection_browser(const std::vector<std::string>& items, const std::string& title,
    const SearchPage* page, const std::shared_ptr<NavigationOrigin>& origin) {
    browser_frame frame;
    frame.title = title; frame.items = items;
    if (page) { frame.page = *page; frame.search = true; }
    browser_history history;
    history.origin = origin;
    history.frames.push_back(std::move(frame));
    open_browser(std::move(history));
}

bool collection_browser_back() {
    for (const auto& item : g_browsers) if (item->Active()) { item->Back(); return true; }
    return false;
}

bool collection_browser_page_action(const std::string& action) {
    for (const auto& item : g_browsers) if (item->Active()) { item->PageAction(action); return true; }
    return false;
}

} // namespace lxmusic
