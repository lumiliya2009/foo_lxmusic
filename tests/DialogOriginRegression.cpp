#include <functional>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <utility>
#include <vector>

// Replace only the window and main-thread scheduling boundaries. No desktop
// windows are created; the owner and completion rules are production code.
struct Window {
    bool alive = true, visible = true, enabled = true;
    Window* popup = this;
};
using HWND = Window*;
static Window main_window, browser_window, other_window, dialog_window;
static HWND foreground = &main_window;
static bool shutting_down = false;
bool IsWindow(HWND w) { return w && w->alive; }
bool IsWindowVisible(HWND w) { return IsWindow(w) && w->visible; }
bool IsWindowEnabled(HWND w) { return IsWindow(w) && w->enabled; }
HWND GetForegroundWindow() { return foreground; }
HWND GetLastActivePopup(HWND w) { return w->popup; }
namespace core_api {
HWND get_main_window() { return &main_window; }
bool is_shutting_down() { return shutting_down; }
}
namespace fb2k {
std::vector<std::function<void()>> pending;
void inMainThread(std::function<void()> action) { pending.push_back(std::move(action)); }
void drain() {
    while (!pending.empty()) {
        auto action = std::move(pending.front());
        pending.erase(pending.begin());
        action();
    }
}
}
#include "../dialog_origin.h"

static unsigned checks = 0;
void check(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}

int main() {
    using namespace lxmusic;
    try {
        struct Session { HWND window; };
        auto session = std::make_shared<Session>(Session{ &browser_window });
        const std::weak_ptr<Session> weak = session;
        unsigned restores = 0, shown = 0;
        DialogOrigin origin{
            [weak]() -> HWND { const auto live = weak.lock(); return live ? live->window : nullptr; },
            [weak, &restores] {
                fb2k::inMainThread([weak, &restores] {
                    const auto live = weak.lock();
                    if (live && can_restore_browser_focus(live->window)) {
                        foreground = live->window;
                        ++restores;
                    }
                });
            }
        };
        check(origin.owner() == &browser_window, "download dialog lost its initiating browser");
        check(DialogOrigin{}.owner() == &main_window, "native playlist download has no default owner");

        // The host still owns the active progress window when on_done runs.
        bool progress_open = true;
        foreground = &dialog_window;
        origin.after_task([&](HWND owner) {
            check(!progress_open, "completion dialog opened before the host closed progress");
            check(owner == &browser_window, "completion dialog used the main window as owner");
            check(restores == 0, "browser stole focus before closing the result dialog");
            ++shown;
            foreground = owner; // Modal result is closed by the user.
        });
        check(shown == 0 && restores == 0, "on_done restored focus synchronously");
        progress_open = false;
        foreground = &main_window; // Host progress teardown activates its owner.
        fb2k::drain();
        check(shown == 1 && restores == 1 && foreground == &browser_window,
            "closing the completion dialog did not return to the collection list");

        // Cancellation has no result dialog, but still restores the browser.
        foreground = &main_window;
        origin.after_task();
        check(foreground == &main_window, "cancel restored before progress teardown finished");
        fb2k::drain();
        check(shown == 1 && restores == 2 && foreground == &browser_window,
            "cancelled download lost the browser");

        // Another dialog/app must keep focus even if a return is pending.
        foreground = &other_window;
        origin.restore(); fb2k::drain();
        check(foreground == &other_window && restores == 2, "background completion stole another window's focus");
        foreground = &browser_window;
        browser_window.enabled = false;
        check(!can_restore_browser_focus(&browser_window), "disabled browser received focus behind a modal");
        browser_window.enabled = true;
        browser_window.popup = &dialog_window;
        check(!can_restore_browser_focus(&browser_window), "visible child dialog lost focus to its owner");
        dialog_window.visible = false;
        check(can_restore_browser_focus(&browser_window), "closed child dialog prevented browser restoration");
        browser_window.popup = &browser_window;
        browser_window.visible = false;
        check(!can_restore_browser_focus(&browser_window), "hidden browser displaced the song playlist");
        browser_window.visible = true;
        browser_window.alive = false;
        check(!can_restore_browser_focus(&browser_window), "destroyed browser was activated");
        check(origin.owner() == &main_window, "destroyed browser handle was passed to a dialog");
        browser_window.alive = true;
        foreground = nullptr;
        check(!can_restore_browser_focus(&browser_window), "missing foreground caused unwanted activation");

        // Opening songs destroys the old browser; Back recreates the same
        // browsing session with a different window while downloads keep running.
        Window returned_browser;
        origin.after_task([&](HWND owner) {
            check(owner == &returned_browser, "download result used the browser destroyed on entering songs");
            foreground = owner;
        });
        session->window = &returned_browser;
        foreground = &main_window;
        fb2k::drain();
        check(restores == 3 && foreground == &returned_browser,
            "download did not follow the browser restored by Back");

        // A download can outlive its browser. Resolve its owner when shown,
        // without reopening a closed browser or keeping a stale HWND alive.
        origin.after_task([&](HWND owner) {
            check(owner == &main_window, "queued result retained a closed browser's owner");
            ++shown;
        });
        session.reset();
        foreground = &main_window;
        fb2k::drain();
        check(shown == 2 && restores == 3 && foreground == &main_window,
            "finishing an orphaned download reopened a closed browser");

        DialogOrigin{}.after_task([&](HWND owner) {
            check(owner == &main_window, "native download completion changed its parent");
            ++shown;
        });
        fb2k::drain();
        check(shown == 3 && foreground == &main_window, "native playlist download focus regressed");
        origin.after_task([&](HWND) { ++shown; });
        shutting_down = true;
        fb2k::drain();
        check(shown == 3 && restores == 3, "shutdown opened a delayed result window");
        std::cout << checks << " dialog ownership/focus regression checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
