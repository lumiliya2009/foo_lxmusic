#include "stdafx.h"

namespace lxmusic {
namespace {

bool is_collection(metadb_handle_ptr item) {
    try {
        std::string encoded;
        Payload payload;
        return uri_to_payload(item->get_path(), encoded, &payload) && payload.is_collection();
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
    void on_init() override { m_callback = std::make_unique<collection_playlist_callback>(); }
    void on_quit() override { m_callback.reset(); }
private:
    std::unique_ptr<collection_playlist_callback> m_callback;
};
static initquit_factory_t<collection_actions_init> g_collection_actions_init;

} // namespace

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
