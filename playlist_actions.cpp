#include "stdafx.h"

namespace lxmusic {

struct NavigationOrigin {
    playlist_position_reference_tracker position;
    metadb_handle_list selected;
    std::function<bool()> restore_browser;
};

namespace {

struct navigation_link {
    playlist_position_reference_tracker child{ false };
    std::shared_ptr<NavigationOrigin> origin;
};
std::vector<std::unique_ptr<navigation_link>> g_navigation;

class navigation_keys : public message_filter_impl_base {
public:
    bool pretranslate_message(MSG* message) override {
        if (message->message != WM_KEYDOWN || message->wParam != VK_BACK ||
            (message->lParam & (1LL << 30)) != 0) return false;
        for (const auto key : { VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN })
            if (GetKeyState(key) & 0x8000) return false;
        const auto focus = GetFocus();
        if (!focus || GetAncestor(focus, GA_ROOT) != core_api::get_main_window()) return false;
        wchar_t class_name[128]{};
        GetClassNameW(focus, class_name, 128);
        if (_wcsicmp(class_name, L"Edit") == 0 || _wcsnicmp(class_name, L"RichEdit", 8) == 0 ||
            _wcsnicmp(class_name, L"ComboBox", 8) == 0) return false;
        const auto type = ui_selection_manager::get()->get_selection_type();
        if (type != contextmenu_item::caller_active_playlist_selection &&
            type != contextmenu_item::caller_active_playlist) return false;
        try {
            auto shortcuts = keyboard_shortcut_manager_v2::get();
            pfc::string8 assigned;
            // Query on each press so edits in Preferences take effect immediately.
            // Once assigned, this action belongs entirely to the host's shortcut dispatcher.
            if (shortcuts->get_key_description_for_action(guid_back, pfc::guid_null, assigned,
                    keyboard_shortcut_manager::TYPE_MAIN, false) ||
                shortcuts->get_key_description_for_action(guid_back, pfc::guid_null, assigned,
                    keyboard_shortcut_manager::TYPE_MAIN, true)) return false;
            // Preserve any user action assigned to Backspace before applying our default.
            if (shortcuts->process_keydown_simple(VK_BACK)) return true;
            return command_back();
        } catch (...) { return false; }
    }
};

bool is_collection(metadb_handle_ptr item) {
    try {
        std::string encoded;
        Payload payload;
        return uri_to_payload(item->get_path(), encoded, &payload) && (payload.is_collection() || payload.is_navigation());
    } catch (...) {
        return false;
    }
}

class collection_actions : public playlist_lock {
public:
    explicit collection_actions(t_size playlist) : m_playlist(playlist) {}

    bool execute_default_action(t_size index) override {
        metadb_handle_ptr item;
        if (!playlist_manager::get()->playlist_get_item_handle(item, m_playlist, index) || !is_collection(item))
            return false;
        std::string encoded;
        Payload payload;
        if (uri_to_payload(item->get_path(), encoded, &payload) && payload.is_navigation()) {
            command_search_page(m_playlist, index);
            return true;
        }
        // Use the activated row, even if several results are selected.
        metadb_handle_list items;
        items.add_item(item);
        command_expand(items);
        return true;
    }

    t_uint32 get_filter_mask() override { return filter_default_action; }
    bool query_items_add(t_size, metadb_handle_list_cref, const bit_array&) override { return true; }
    bool query_items_reorder(const t_size*, t_size) override { return true; }
    bool query_items_remove(const bit_array&, bool) override { return true; }
    bool query_item_replace(t_size, const metadb_handle_ptr&, const metadb_handle_ptr&) override { return true; }
    bool query_playlist_rename(const char*, t_size) override { return true; }
    bool query_playlist_remove() override { return true; }
    void on_playlist_index_change(t_size index) override { m_playlist = index; }
    void on_playlist_remove() override { m_playlist = SIZE_MAX; }
    void get_lock_name(pfc::string_base& out) override { out = "LX 集合导航"; }
    void show_ui() override {}

private:
    t_size m_playlist;
};

// Also support collection results copied into another playlist. Defer lock
// installation until after the playlist notification has finished, and rescan
// current indices in case a playlist was moved or removed in the meantime.
bool g_refresh_pending = false;
void schedule_collection_actions() {
    if (g_refresh_pending) return;
    g_refresh_pending = true;
    fb2k::inMainThread([] {
        g_refresh_pending = false;
        if (core_api::is_shutting_down()) return;
        auto playlists = playlist_manager::get();
        for (t_size index = 0; index < playlists->get_playlist_count(); ++index)
            install_collection_actions(index);
    });
}

class collection_playlist_callback : public playlist_callback_impl_base {
public:
    collection_playlist_callback() : playlist_callback_impl_base(flag_on_items_added | flag_on_items_replaced) {}
    void on_items_added(t_size, t_size, metadb_handle_list_cref items, const bit_array&) override {
        for (t_size i = 0; i < items.get_count(); ++i) {
            if (is_collection(items[i])) { schedule_collection_actions(); break; }
        }
    }
    void on_items_replaced(t_size, const bit_array&,
        const pfc::list_base_const_t<t_on_items_replaced_entry>& items) override {
        for (t_size i = 0; i < items.get_count(); ++i) {
            if (is_collection(items[i].m_new)) { schedule_collection_actions(); break; }
        }
    }
};

class collection_actions_init : public initquit {
public:
    void on_init() override {
        m_callback = std::make_unique<collection_playlist_callback>();
        m_keys = std::make_unique<navigation_keys>();
    }
    void on_quit() override { m_keys.reset(); g_navigation.clear(); m_callback.reset(); }
private:
    std::unique_ptr<collection_playlist_callback> m_callback;
    std::unique_ptr<navigation_keys> m_keys;
};
static initquit_factory_t<collection_actions_init> g_collection_actions_init;

} // namespace

std::shared_ptr<NavigationOrigin> capture_navigation_origin(metadb_handle_list_cref items) {
    auto playlists = playlist_manager::get();
    const auto active = playlists->get_active_playlist();
    if (active == pfc_infinite || items.get_count() == 0) return {};
    auto focus = playlists->playlist_get_focus_item(active);
    metadb_handle_ptr item;
    if (!playlists->playlist_get_item_handle(item, active, focus) || items.find_item(item) == pfc_infinite) {
        focus = pfc_infinite;
        for (t_size i = 0; i < playlists->playlist_get_item_count(active); ++i) {
            if (playlists->playlist_get_item_handle(item, active, i) && items.find_item(item) != pfc_infinite) {
                focus = i; break;
            }
        }
        if (focus == pfc_infinite) return {};
    }
    auto origin = std::make_shared<NavigationOrigin>();
    origin->position.m_playlist = active;
    origin->position.m_item = focus;
    playlists->playlist_get_selected_items(active, origin->selected);
    return origin;
}

std::shared_ptr<NavigationOrigin> capture_browser_navigation(std::function<bool()> restore) {
    auto origin = std::make_shared<NavigationOrigin>();
    origin->restore_browser = std::move(restore);
    return origin;
}

void reset_playlist_navigation(t_size playlist) {
    g_navigation.erase(std::remove_if(g_navigation.begin(), g_navigation.end(), [playlist](const auto& link) {
        return link->child.m_playlist == playlist || link->child.m_playlist == pfc_infinite ||
            (!link->origin->restore_browser && link->origin->position.m_playlist == pfc_infinite);
    }), g_navigation.end());
}

void add_expanded_payloads(const std::vector<std::string>& payloads, const std::string& playlist_name,
    const std::shared_ptr<NavigationOrigin>& origin) {
    auto playlists = playlist_manager::get();
    auto preserve = origin ? origin->position.m_playlist : pfc_infinite;
    const auto destination = playlists->find_playlist(playlist_name.c_str());
    // Reusing an ancestor's name must not overwrite its results or create a back-navigation cycle.
    auto ancestor = preserve;
    for (size_t step = 0; ancestor != pfc_infinite && step <= g_navigation.size(); ++step) {
        if (ancestor == destination) { preserve = destination; break; }
        const auto link = std::find_if(g_navigation.begin(), g_navigation.end(), [ancestor](const auto& item) {
            return item->child.m_playlist == ancestor;
        });
        if (link == g_navigation.end()) break;
        ancestor = (*link)->origin->position.m_playlist;
    }
    const auto child = add_payloads_to_playlist(payloads, playlist_name, preserve,
        origin && static_cast<bool>(origin->restore_browser));
    if (!origin || (!origin->restore_browser &&
        (origin->position.m_playlist == pfc_infinite || child == origin->position.m_playlist))) return;
    auto link = std::make_unique<navigation_link>();
    link->child.m_playlist = child;
    link->origin = origin;
    g_navigation.push_back(std::move(link));
}

bool command_back() {
    if (collection_browser_back()) return true;
    auto playlists = playlist_manager::get();
    const auto active = playlists->get_active_playlist();
    const auto link = std::find_if(g_navigation.begin(), g_navigation.end(), [active](const auto& item) {
        return active != pfc_infinite && item->child.m_playlist == active &&
            (item->origin->restore_browser || item->origin->position.m_playlist != pfc_infinite);
    });
    if (link == g_navigation.end()) return false;
    return restore_navigation_origin((*link)->origin);
}

bool restore_navigation_origin(const std::shared_ptr<NavigationOrigin>& origin) {
    if (!origin) return false;
    if (origin->restore_browser) return origin->restore_browser();
    if (origin->position.m_playlist == pfc_infinite) return false;
    auto playlists = playlist_manager::get();
    const auto parent = origin->position.m_playlist;
    const auto count = playlists->playlist_get_item_count(parent);
    pfc::bit_array_bittable selected(count);
    for (t_size i = 0; i < count; ++i) {
        metadb_handle_ptr item;
        if (playlists->playlist_get_item_handle(item, parent, i) && origin->selected.find_item(item) != pfc_infinite)
            selected.set(i, true);
    }
    playlists->set_active_playlist(parent);
    playlists->playlist_set_selection(parent, pfc::bit_array_true(), selected);
    const auto focus = origin->position.m_item;
    if (focus < count) {
        playlists->playlist_set_focus_item(parent, focus);
        playlists->playlist_ensure_visible(parent, focus);
    }
    return true;
}

void install_collection_actions(t_size playlist) {
    auto playlists = playlist_manager::get();
    if (playlists->playlist_lock_is_present(playlist)) return;
    metadb_handle_list items;
    playlists->playlist_get_all_items(playlist, items);
    for (t_size index = 0; index < items.get_count(); ++index) {
        if (!is_collection(items[index])) continue;
        playlists->playlist_lock_install(playlist, fb2k::service_new<collection_actions>(playlist));
        break;
    }
}

} // namespace lxmusic
