#pragma once

namespace lxmusic {

// Resolve the initiating window when it is needed: a download may outlive it.
struct DialogOrigin {
    std::function<HWND()> resolve_window;
    std::function<void()> restore_focus;

    HWND owner() const {
        const auto window = resolve_window ? resolve_window() : nullptr;
        return window && ::IsWindow(window) ? window : core_api::get_main_window();
    }

    void restore() const { if (restore_focus) restore_focus(); }

    void after_task(std::function<void(HWND)> show_result = {}) const {
        // on_done runs before the host closes its progress window. Wait until
        // that window has finished restoring its own owner before showing ours.
        fb2k::inMainThread([origin = *this, show_result = std::move(show_result)] {
            if (core_api::is_shutting_down()) return;
            if (show_result) show_result(origin.owner());
            origin.restore();
        });
    }
};

inline bool can_restore_browser_focus(HWND window) {
    if (!window || !::IsWindow(window) || !::IsWindowVisible(window) || !::IsWindowEnabled(window)) return false;
    const auto foreground = ::GetForegroundWindow();
    // Do not steal focus from another app, another browser, or a child dialog.
    if (foreground != window && foreground != core_api::get_main_window()) return false;
    const auto popup = ::GetLastActivePopup(window);
    return popup == window || !::IsWindowVisible(popup);
}

} // namespace lxmusic
