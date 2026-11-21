#include <algorithm>
#include <cwchar>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include <cstdint>
#include <functional>
using t_size = size_t;
constexpr t_size pfc_infinite = SIZE_MAX;
using HWND = int;
constexpr unsigned WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, VK_BACK = 8, VK_SHIFT = 16,
    VK_CONTROL = 17, VK_MENU = 18, VK_LWIN = 91, VK_RWIN = 92, GA_ROOT = 2;
struct MSG { unsigned message = WM_KEYDOWN; uintptr_t wParam = VK_BACK; intptr_t lParam = 0; };
static HWND focused_window = 2, root_window = 1;
static unsigned modifier = 0;
static std::wstring control_class = L"PlaylistView";
static short GetKeyState(unsigned key) { return key == modifier ? short(0x8000) : 0; }
static HWND GetFocus() { return focused_window; }
static HWND GetAncestor(HWND, unsigned) { return root_window; }
static int GetClassNameW(HWND, wchar_t* out, int count) { wcsncpy_s(out, count, control_class.c_str(), _TRUNCATE); return int(control_class.size()); }
struct core_api { static HWND get_main_window() { return 1; } };
struct message_filter_impl_base { virtual ~message_filter_impl_base() = default; virtual bool pretranslate_message(MSG*) { return false; } };
struct contextmenu_item { enum { caller_active_playlist_selection = 1, caller_active_playlist = 2 }; };
struct ui_selection_manager {
    int type = contextmenu_item::caller_active_playlist_selection;
    static ui_selection_manager* get() { static ui_selection_manager instance; return &instance; }
    int get_selection_type() { return type; }
};
using metadb_handle_ptr = std::shared_ptr<std::string>;
struct metadb_handle_list : std::vector<metadb_handle_ptr> {
    size_t get_count() const { return size(); }
    void add_item(metadb_handle_ptr item) { push_back(item); }
    size_t find_item(metadb_handle_ptr item) const { auto p = std::find(begin(), end(), item); return p == end() ? pfc_infinite : size_t(p - begin()); }
};
using metadb_handle_list_cref = const metadb_handle_list&;
struct bit_array {
    virtual ~bit_array() = default;
    virtual bool get(size_t i) const = 0;
    size_t find_first(bool value, size_t from, size_t count) const { for (size_t i = from; i < count; ++i) if (get(i) == value) return i; return count; }
    size_t find_next(bool value, size_t from, size_t count) const { return find_first(value, from + 1, count); }
};
namespace pfc {
using string8 = std::string;
constexpr int guid_null = 0;
template<typename T> struct list_base_const_t : std::vector<T> { size_t get_count() const { return this->size(); } };
struct bit_array_true : bit_array { bool get(size_t) const override { return true; } };
struct bit_array_bittable : bit_array {
    std::vector<bool> bits;
    explicit bit_array_bittable(size_t count) : bits(count) {}
    bool get(size_t i) const override { return i < bits.size() && bits[i]; }
    void set(size_t i, bool value) { bits.at(i) = value; }
};
}
namespace lxmusic { constexpr int guid_back = 100; }
struct keyboard_shortcut_manager {
    enum shortcut_type { TYPE_MAIN };
    bool local_back = false, global_back = false, consume_back = false, fail_query = false;
    unsigned dispatched = 0;
    std::function<void()> action;
    bool get_key_description_for_action(int command, int subcommand, pfc::string8& description,
        shortcut_type type, bool global) {
        if (fail_query) throw std::runtime_error("shortcut service unavailable");
        if (command != lxmusic::guid_back || subcommand != pfc::guid_null || type != TYPE_MAIN)
            throw std::runtime_error("queried the wrong shortcut action");
        const bool assigned = global ? global_back : local_back;
        description = assigned ? "Alt+Left" : "";
        return assigned;
    }
};
struct keyboard_shortcut_manager_v2 : keyboard_shortcut_manager {
    static keyboard_shortcut_manager_v2* get() { static keyboard_shortcut_manager_v2 instance; return &instance; }
    bool process_keydown_simple(unsigned key) {
        if (key != VK_BACK) throw std::runtime_error("unexpected shortcut dispatch");
        ++dispatched;
        if (!consume_back) return false;
        if (action) action();
        return true;
    }
};
struct order_helper {
    static size_t g_find_reverse(const size_t* order, size_t value) { for (size_t i = 0;; ++i) if (order[i] == value) return i; }
};
struct playlist_callback_impl_base {
    struct t_on_items_replaced_entry {};
    inline static std::vector<playlist_callback_impl_base*> listeners;
    explicit playlist_callback_impl_base(unsigned) { listeners.push_back(this); }
    virtual ~playlist_callback_impl_base() { listeners.erase(std::remove(listeners.begin(), listeners.end(), this), listeners.end()); }
    virtual void on_items_added(t_size, t_size, const pfc::list_base_const_t<metadb_handle_ptr>&, const bit_array&) {}
    virtual void on_items_reordered(t_size, const t_size*, t_size) {}
    virtual void on_items_removed(t_size, const bit_array&, t_size, t_size) {}
    virtual void on_items_replaced(t_size, const bit_array&, const pfc::list_base_const_t<t_on_items_replaced_entry>&) {}
    virtual void on_playlist_created(t_size, const char*, t_size) {}
    virtual void on_playlists_reorder(const t_size*, t_size) {}
    virtual void on_playlists_removed(const bit_array&, t_size, t_size) {}
};
struct playlist_manager {
    struct Playlist { std::string name; metadb_handle_list items; std::vector<bool> selected; size_t focus = pfc_infinite; };
    std::vector<Playlist> lists;
    size_t active = pfc_infinite, visible = pfc_infinite;
    static playlist_manager* get() { static playlist_manager instance; return &instance; }
    size_t get_active_playlist() { return active; }
    void set_active_playlist(size_t index) { (void)lists.at(index); active = index; }
    size_t playlist_get_item_count(size_t index) { return lists.at(index).items.size(); }
    size_t playlist_get_focus_item(size_t index) { return lists.at(index).focus; }
    bool playlist_get_item_handle(metadb_handle_ptr& item, size_t index, size_t row) {
        auto& list = lists.at(index); if (row >= list.items.size()) return false; item = list.items[row]; return true;
    }
    void playlist_get_selected_items(size_t index, metadb_handle_list& out) {
        auto& list = lists.at(index); for (size_t i = 0; i < list.items.size(); ++i) if (list.selected[i]) out.push_back(list.items[i]);
    }
    size_t find_playlist(const char* name) { for (size_t i = 0; i < lists.size(); ++i) if (lists[i].name == name) return i; return pfc_infinite; }
    void playlist_set_selection(size_t index, const bit_array& affected, const bit_array& selected) {
        auto& list = lists.at(index); for (size_t i = 0; i < list.items.size(); ++i) if (affected.get(i)) list.selected[i] = selected.get(i);
    }
    void playlist_set_focus_item(size_t index, size_t row) { lists.at(index).focus = row; }
    void playlist_ensure_visible(size_t, size_t row) { visible = row; }
    size_t create(std::string name, const std::vector<std::string>& values) {
        const auto index = lists.size(); lists.push_back({std::move(name)}); fill(index, values);
        for (auto* listener : playlist_callback_impl_base::listeners) listener->on_playlist_created(index, "", 0);
        return index;
    }
    void fill(size_t index, const std::vector<std::string>& values) {
        auto& list = lists.at(index); list.items.clear(); list.selected.assign(values.size(), false); list.focus = 0;
        for (const auto& value : values) list.items.push_back(std::make_shared<std::string>(value));
        if (!values.empty()) list.selected[0] = true;
    }
    void reorder(const std::vector<size_t>& order) {
        auto old = lists; for (size_t i = 0; i < order.size(); ++i) lists[i] = old[order[i]];
        if (active != pfc_infinite) active = order_helper::g_find_reverse(order.data(), active);
        for (auto* listener : playlist_callback_impl_base::listeners) listener->on_playlists_reorder(order.data(), order.size());
    }
    void remove(size_t index) {
        const auto old = lists.size(); pfc::bit_array_bittable mask(old); mask.set(index, true);
        lists.erase(lists.begin() + index);
        if (active == index) active = pfc_infinite; else if (active > index && active != pfc_infinite) --active;
        for (auto* listener : playlist_callback_impl_base::listeners) listener->on_playlists_removed(mask, old, lists.size());
    }
    void reorder_items(size_t index, const std::vector<size_t>& order) {
        auto old = lists.at(index); auto& list = lists[index];
        for (size_t i = 0; i < order.size(); ++i) { list.items[i] = old.items[order[i]]; list.selected[i] = old.selected[order[i]]; }
        list.focus = order_helper::g_find_reverse(order.data(), old.focus);
        for (auto* listener : playlist_callback_impl_base::listeners) listener->on_items_reordered(index, order.data(), order.size());
    }
};
namespace lxmusic {
struct NavigationOrigin;
bool command_back();
bool restore_navigation_origin(const std::shared_ptr<NavigationOrigin>& origin);
static bool browser_active = false;
static unsigned browser_back_count = 0;
bool collection_browser_back() { if (!browser_active) return false; ++browser_back_count; return true; }
void reset_playlist_navigation(t_size);
t_size add_payloads_to_playlist(const std::vector<std::string>& values, const std::string& name, t_size preserve, bool create_new = false) {
    auto* pm = playlist_manager::get(); auto index = pm->find_playlist(name.c_str());
    if (create_new || index == pfc_infinite || index == preserve) index = pm->create(name, values);
    else pm->fill(index, values);
    reset_playlist_navigation(index); pm->set_active_playlist(index); return index;
}
}
