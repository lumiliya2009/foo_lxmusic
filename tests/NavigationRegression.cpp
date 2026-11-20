static int checks;
static void check(bool value, const char* reason) { ++checks; if (!value) throw std::runtime_error(reason); }
int main() {
    using namespace lxmusic;
    try {
        auto* pm = playlist_manager::get();
        auto parent = pm->create("search", {"album-a", "album-b", "album-c"}); pm->set_active_playlist(parent);
        pm->lists[parent].selected = {false, true, true}; pm->lists[parent].focus = 1;
        metadb_handle_list opened; opened.add_item(pm->lists[parent].items[1]);
        auto origin = capture_navigation_origin(opened);
        check(origin && !command_back(), "root should not have a back action");
        add_expanded_payloads({"song-a", "song-b"}, "album", origin);
        auto child = pm->active;
        check(child != parent && command_back(), "album should return to its parent");
        check(pm->active == parent && pm->lists[parent].focus == 1 && pm->visible == 1, "parent focus/visibility was not restored");
        check(pm->lists[parent].selected == std::vector<bool>({false, true, true}), "parent selection was not restored");
        pm->lists[parent].name = "renamed"; pm->reorder_items(parent, {2, 0, 1}); pm->reorder({1, 0});
        parent = 1; child = 0; pm->set_active_playlist(child);
        check(command_back() && pm->active == parent && pm->lists[parent].focus == 2, "rename or reorder invalidated parent navigation");
        check(pm->lists[parent].selected == std::vector<bool>({true, false, true}), "reordering changed restored selection");
        pm->set_active_playlist(child);
        navigation_keys keys; MSG message;
        for (const auto key : {VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN}) {
            modifier = key; check(!keys.pretranslate_message(&message) && pm->active == child, "modified Backspace was intercepted");
        }
        modifier = 0;
        for (const auto* name : {L"Edit", L"RichEdit50W", L"ComboBox"}) {
            control_class = name; check(!keys.pretranslate_message(&message), "text entry Backspace was intercepted");
        }
        control_class = L"PlaylistView";
        root_window = 3; check(!keys.pretranslate_message(&message), "dialog Backspace was intercepted"); root_window = 1;
        ui_selection_manager::get()->type = 9; check(!keys.pretranslate_message(&message), "other panel Backspace was intercepted");
        ui_selection_manager::get()->type = contextmenu_item::caller_active_playlist_selection;
        message.message = WM_KEYUP; check(!keys.pretranslate_message(&message), "key-up navigated"); message.message = WM_KEYDOWN;
        message.wParam = 13; check(!keys.pretranslate_message(&message), "another key navigated"); message.wParam = VK_BACK;
        message.lParam = intptr_t(1) << 30; check(!keys.pretranslate_message(&message), "held Backspace skipped multiple levels"); message.lParam = 0;
        auto* shortcuts = keyboard_shortcut_manager_v2::get();
        check(shortcuts->dispatched == 0, "excluded key contexts reached shortcut dispatch");
        shortcuts->local_back = true;
        check(!keys.pretranslate_message(&message) && pm->active == child, "custom shortcut did not disable default Backspace");
        check(shortcuts->dispatched == 0, "custom shortcut was dispatched twice");
        check(command_back() && pm->active == parent, "custom shortcut command could not return");
        pm->set_active_playlist(child); shortcuts->local_back = false; shortcuts->global_back = true;
        check(!keys.pretranslate_message(&message) && pm->active == child, "global shortcut did not disable default Backspace");
        shortcuts->global_back = false; shortcuts->consume_back = true;
        unsigned other_actions = 0; shortcuts->action = [&] { ++other_actions; };
        check(keys.pretranslate_message(&message) && pm->active == child && other_actions == 1,
            "default Backspace overrode an existing user action");
        shortcuts->action = [&] { ++other_actions; command_back(); };
        check(keys.pretranslate_message(&message) && pm->active == parent && other_actions == 2,
            "handled shortcut was executed again as a default");
        pm->set_active_playlist(child); shortcuts->consume_back = false; shortcuts->action = {}; shortcuts->fail_query = true;
        check(!keys.pretranslate_message(&message) && pm->active == child, "shortcut query failure forced default navigation");
        shortcuts->fail_query = false;
        check(keys.pretranslate_message(&message) && pm->active == parent, "plain playlist Backspace did not return");
        check(!keys.pretranslate_message(&message), "Backspace with no parent was swallowed");
        // A pending expansion retains the original parent even after switching tabs.
        origin = capture_navigation_origin(opened); pm->set_active_playlist(child);
        add_expanded_payloads({"other-song"}, "other-album", origin);
        check(command_back() && pm->active == parent, "async completion captured the wrong parent");
        // An album named like an ancestor must open separately without clearing that ancestor.
        origin = capture_navigation_origin(opened);
        const auto originalCount = pm->lists[parent].items.size();
        add_expanded_payloads({"nested-collection"}, "renamed", origin);
        const auto nested = pm->active;
        check(nested != parent && pm->lists[parent].items.size() == originalCount, "same-name result overwrote its parent");
        metadb_handle_list nestedItem; nestedItem.add_item(pm->lists[nested].items[0]);
        auto nestedOrigin = capture_navigation_origin(nestedItem);
        add_expanded_payloads({"leaf"}, "renamed", nestedOrigin);
        check(pm->active != parent && pm->active != nested, "reusing ancestor name created a navigation cycle");
        check(command_back() && pm->active == nested && command_back() && pm->active == parent, "nested back chain failed");
        pm->set_active_playlist(child); reset_playlist_navigation(child);
        check(!command_back(), "reused search results retained stale history");
        pm->set_active_playlist(nested); pm->remove(parent);
        check(!command_back(), "deleted parent led to an unrelated playlist");
        g_navigation.clear(); origin.reset(); nestedOrigin.reset();
        check(playlist_callback_impl_base::listeners.empty(), "navigation trackers were not released");
        std::cout << checks << " navigation regression checks passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
