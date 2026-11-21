#include "stdafx.h"
#include <cstring>
#include <atomic>
#include <ctime>

namespace lxmusic {

namespace {

struct async_state {
    BridgeResult result;
    std::vector<std::string> items;
    std::string error;
    SearchPage page;
};

const GUID guid_search_page = { 0x85d64b46, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
struct page_request {
    playlist_position_reference_tracker position;
    metadb_handle_ptr control;
    std::string original_state;
    bool owned = false;
};
std::vector<std::weak_ptr<page_request>> g_page_requests;
std::atomic_bool g_update_pending{ false };
struct update_guard { ~update_guard() { g_update_pending = false; } };
cfg_string g_last_update_check({ 0x628a3dae, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
UINT_PTR g_update_timer = 0;
std::time_t g_next_update_attempt = 0;
bool g_updates_stopping = false;
constexpr std::time_t automatic_update_interval = 60 * 60;

void CALLBACK update_timer(HWND, UINT, UINT_PTR, DWORD) {
    if (g_updates_stopping || !automatic_updates_enabled() || g_update_pending) return;
    const auto now = std::time(nullptr);
    if (now < g_next_update_attempt) return;
    std::time_t last = 0;
    try { last = static_cast<std::time_t>(std::stoll(g_last_update_check.get().c_str())); } catch (...) {}
    if (last <= now && now - last < automatic_update_interval) return;
    g_next_update_attempt = now + 10 * 60;
    command_check_update(true);
}

bool owns_search_page(t_size playlist, const std::string& state) {
    pfc::array_t<uint8_t> data;
    if (!playlist_manager_v2::get()->playlist_get_property(playlist, guid_search_page, data) || data.get_size() != state.size()) return false;
    return !state.empty() && std::memcmp(data.get_ptr(), state.data(), state.size()) == 0;
}

void mark_search_page(t_size playlist, const SearchPage& page) {
    const auto state = encode_search_state(page);
    pfc::array_t<uint8_t> data;
    data.set_size(state.size());
    std::memcpy(data.get_ptr(), state.data(), state.size());
    playlist_manager_v2::get()->playlist_set_property(playlist, guid_search_page, data);
}

bool download_search_context(metadb_handle_list_cref selected, SearchPage& page) {
    try {
        if (selected.get_count() == 0) return false;
        auto playlists = playlist_manager::get();
        const auto active = playlists->get_active_playlist();
        if (active == pfc_infinite) return false;
        pfc::array_t<uint8_t> property;
        if (!playlist_manager_v2::get()->playlist_get_property(active, guid_search_page, property) || property.get_size() == 0 || property.get_size() > 16384) return false;
        page = decode_search_state(std::string(reinterpret_cast<const char*>(property.get_ptr()), property.get_size()));
        if (page.options.type != "track" && page.options.type != "album") return false;
        metadb_handle_list items;
        playlists->playlist_get_all_items(active, items);
        for (t_size i = 0; i < selected.get_count(); ++i) {
            bool belongs = false;
            for (t_size j = 0; j < items.get_count(); ++j) if (items[j] == selected[i]) { belongs = true; break; }
            if (!belongs) return false;
            std::string encoded;
            Payload payload;
            if (!uri_to_payload(selected[i]->get_path(), encoded, &payload)) return false;
            if (!payload.is_navigation() && (payload.get(pf_kind) != page.options.type || payload.get(pf_platform_id) != page.options.site)) return false;
        }
        return true;
    } catch (...) { return false; }
}

struct quality_resume_state {
    metadb_handle_ptr target;
    double position = 0;
    bool restore_paused = false;
    bool pending = false;
    bool scheduled = false;
    unsigned attempts = 0;
};

quality_resume_state g_quality_resume;

void clear_quality_resume() {
    g_quality_resume = {};
}

void schedule_quality_resume() {
    if (!g_quality_resume.pending || g_quality_resume.scheduled) return;
    g_quality_resume.scheduled = true;
    fb2k::splitTask([] {
        Sleep(120);
        fb2k::inMainThread([] {
            g_quality_resume.scheduled = false;
            if (!g_quality_resume.pending) return;
            try {
                auto control = playback_control::get();
                metadb_handle_ptr current;
                if (!control->get_now_playing(current) || current != g_quality_resume.target) {
                    clear_quality_resume();
                    return;
                }
                if (!control->playback_can_seek() && ++g_quality_resume.attempts < 20) {
                    schedule_quality_resume();
                    return;
                }
                const auto position = g_quality_resume.position;
                const auto paused = g_quality_resume.restore_paused;
                clear_quality_resume();
                control->playback_seek(position);
                control->pause(paused);
            } catch (...) {
                clear_quality_resume();
            }
        });
    });
}

void arm_quality_resume(metadb_handle_ptr target, double position, bool paused) {
    clear_quality_resume();
    g_quality_resume.target = target;
    g_quality_resume.position = std::max(0.0, position);
    g_quality_resume.restore_paused = paused;
    g_quality_resume.pending = true;
}

class quality_resume_callback : public play_callback_static {
public:
    unsigned get_flags() override {
        return flag_on_playback_new_track | flag_on_playback_stop;
    }
    void on_playback_new_track(metadb_handle_ptr track) override {
        if (!g_quality_resume.pending) return;
        if (track != g_quality_resume.target) {
            clear_quality_resume();
            return;
        }
        schedule_quality_resume();
    }
    void on_playback_stop(play_control::t_stop_reason reason) override {
        if (reason != play_control::stop_reason_starting_another) clear_quality_resume();
    }
    void on_playback_starting(play_control::t_track_command, bool) override {}
    void on_playback_seek(double) override {}
    void on_playback_pause(bool) override {}
    void on_playback_edited(metadb_handle_ptr) override {}
    void on_playback_dynamic_info(const file_info&) override {}
    void on_playback_dynamic_info_track(const file_info&) override {}
    void on_playback_time(double) override {}
    void on_volume_change(float) override {}
};

static play_callback_static_factory_t<quality_resume_callback> g_quality_resume_callback;

template<typename Worker, typename Done>
void run_task(const char* title, Worker worker, Done done, bool showItem = false, bool silentErrors = false) {
    (void)title;
    (void)showItem;
    auto state = std::make_shared<async_state>();
    fb2k::splitTask([state, worker, done, silentErrors] {
        class silent_status final : public threaded_process_status {} status;
        try { worker(*state, status, fb2k::noAbort); }
        catch (const std::exception& ex) { state->error = ex.what(); }
        catch (...) { state->error = "LX 音源桥接发生未知异常"; }
        fb2k::inMainThread([state, done, silentErrors] {
            try {
                if (!state->error.empty()) {
                    if (!silentErrors) popup_message::g_complain("LX 在线音乐操作失败", state->error.c_str());
                }
                else done(*state);
            } catch (const std::exception& ex) {
                if (!silentErrors) popup_message::g_complain("LX 在线音乐操作失败", ex.what());
            } catch (...) {}
        });
    });
}

std::string type_label(const std::string& type) {
    if (type == "track") return "单曲";
    if (type == "album") return "专辑";
    if (type == "artist") return "歌手";
    if (type == "playlist") return "歌单";
    if (type == "featured") return "精选歌单";
    if (type == "rankings") return "榜单";
    if (type == "categories") return "歌单分类";
    if (type == "discover") return "随便看看";
    return type;
}

std::string site_label(const std::string& site) {
    if (site == "qq") return "QQ音乐";
    if (site == "kugou") return "酷狗音乐";
    if (site == "netease") return "网易云音乐";
    if (site == "kuwo") return "酷我音乐";
    if (site == "migu") return "咪咕音乐";
    if (site == "qishui") return "汽水音乐";
    return site;
}

std::string search_playlist_name(const SearchOptions& options) {
    auto name = "LX " + site_label(options.site) + " " + type_label(options.type);
    return options.keyword.empty() ? name : name + " - " + options.keyword;
}

void fetch_search_page(async_state& state, const SearchOptions& options, int page, abort_callback& abort,
    const SearchPage* previous = nullptr, bool last = false) {
    const auto read = [&](int number) {
        abort.check();
        const auto keyword = options.keyword.empty() ? std::string("-") : base64url_encode_text(options.keyword);
        auto result = run_bridge({ "search", options.site, options.type, keyword,
            std::to_string(number), "30", options.quality, options.source }, abort);
        auto items = bridge_items(result);
        if (items.empty()) return false;
        state.page = search_page_from_bridge(result, options, number);
        state.result = std::move(result);
        state.items = std::move(items);
        return true;
    };
    if (last && previous) {
        const auto actual = find_last_search_page(previous->page, page, read);
        if ((state.items.empty() || state.page.page != actual) && !read(actual))
            throw std::runtime_error("站点没有返回可用的搜索结果，原列表已保留");
        if (actual < page) { state.page.total_pages = actual; state.page.total_items = -1; state.page.has_next = false; }
    } else if (!read(page)) throw std::runtime_error("第 " + std::to_string(page) + " 页没有搜索结果，原列表已保留");
    if (previous && previous->total_pages >= state.page.page && state.page.total_pages > previous->total_pages) {
        state.page.total_pages = previous->total_pages;
        state.page.total_items = -1;
        state.page.has_next = state.page.page < state.page.total_pages;
    }
    append_search_controls(state.items, state.page);
}

void show_new_search(async_state& state) {
    const auto title = search_playlist_name(state.page.options);
    if (has_collection_results(state.items)) show_collection_browser(state.items, title, &state.page);
    else {
        const auto index = add_payloads_to_playlist(state.items, title, pfc_infinite, true);
        mark_search_page(index, state.page);
    }
    if (state.page.options.type != "discover") remember_search(state.page.options);
}

std::vector<SourceOption> source_options(const BridgeResult& result) {
    std::vector<SourceOption> sources;
    for (const auto& line : result.lines) {
        const auto& f = line.fields;
        if (f.size() >= 4 && f[0] == "SOURCE") {
            if (f[1].size() != 32 || f[1].find_first_not_of("0123456789abcdef") != std::string::npos) continue;
            SourceOption source;
            source.id = f[1];
            source.name = base64url_decode(f[2]);
            source.active = f[3] == "1";
            if (f.size() >= 5) source.details = base64url_decode(f[4]);
            source.url_available = f.size() >= 6 && f[5] == "1";
            if (f.size() >= 7 && f[6].size() == 64 && f[6].find_first_not_of("0123456789abcdef") == std::string::npos)
                source.revision = f[6];
            if (f.size() >= 8) source.url = base64url_decode(f[7]);
            sources.push_back(std::move(source));
        } else if (f.size() >= 5 && f[0] == "SITE") {
            if (f[4] == "unavailable") continue;
            // Keep source-declared platforms extensible, with safe CLI IDs.
            if (f[2].empty() || f[2].size() > 32 || f[2].find_first_not_of("abcdefghijklmnopqrstuvwxyz0123456789_-") != std::string::npos) continue;
            for (auto& source : sources) if (source.id == f[1]) {
                SourceSite site;
                site.id = f[2];
                site.label = base64url_decode(f[3]);
                site.collections = f[4].find("album") != std::string::npos;
                site.searchable = f[4] != "unavailable";
                site.featured = f[4].find("featured") != std::string::npos;
                site.rankings = f[4].find("rankings") != std::string::npos;
                site.categories = f[4].find("categories") != std::string::npos;
                site.discover = f[4].find("discover") != std::string::npos;
                source.sites.push_back(std::move(site));
            }
        }
    }
    return sources;
}

void start_bridge_message(const char* title, std::vector<std::string> args, std::function<void()> next = {}) {
    run_task(title,
        [args = std::move(args)](async_state& state, threaded_process_status& status, abort_callback& abort) {
            status.set_item("正在调用 LX 音源桥接…");
            state.result = run_bridge(args, abort);
        },
        [next](async_state& state) {
            if (next) { next(); return; }
            auto message = bridge_message(state.result);
            if (message.empty()) message = "操作完成";
            show_status(message);
        }, true);
}

void save_source_edit(const std::string& id, const std::string& revision,
    std::string title, std::string script) {
    auto state = std::make_shared<async_state>();
    fb2k::splitTask([state, id, revision, title, script] {
        try {
            state->result = run_bridge({ "edit-source", id, revision }, fb2k::noAbort, {}, &script);
        } catch (const std::exception& ex) {
            state->error = ex.what();
        } catch (...) {
            state->error = "保存音源时发生未知错误";
        }
        fb2k::inMainThread([state, id, revision, title, script] {
            if (state->error.empty()) {
                show_status("音源已保存");
                command_manage_sources();
                return;
            }
            popup_message::g_complain("保存音源失败", state->error.c_str());
            std::string retry_script = script;
            std::string retry_revision = revision;
            if (show_source_editor(title + "（校验失败，内容已保留）", retry_script, retry_revision))
                save_source_edit(id, retry_revision, title, std::move(retry_script));
        });
    });
}

void edit_source(const SourceOption& source) {
    run_task("读取音源脚本",
        [id = source.id](async_state& state, threaded_process_status&, abort_callback& abort) {
            state.result = run_bridge({ "source-script", id }, abort);
        },
        [source](async_state& state) {
            std::string script, revision;
            for (const auto& line : state.result.lines) {
                if (line.fields.size() >= 3 && line.fields[0] == "SCRIPT") {
                    script = base64url_decode(line.fields[1]);
                    revision = line.fields[2];
                    break;
                }
            }
            if (script.empty() || revision.size() != 64) throw std::runtime_error("音源脚本读取失败");
            if (show_source_editor("编辑 LX 音源 - " + source.name, script, revision))
                save_source_edit(source.id, revision, source.name, std::move(script));
        }, true);
}

void update_source(const SourceOption& source) {
    const std::string warning = "检查音源更新：\n\n" + source.name +
        "\n\n如果音源提供可下载的新脚本，当前脚本（包括手动修改的 Cookies）会被替换。是否继续？";
    if (MessageBoxW(core_api::get_main_window(), pfc::stringcvt::string_wide_from_utf8(warning.c_str()),
        L"检查音源更新", MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) != IDYES) {
        command_manage_sources();
        return;
    }
    show_status("正在检查音源更新：" + source.name);
    run_task("检查音源更新",
        [source](async_state& state, threaded_process_status&, abort_callback& abort) {
            state.result = run_bridge({ "update-source", source.id, source.revision }, abort,
                [](const BridgeLine& line) {
                    if (line.fields.size() < 2 || line.fields[0] != "STATUS") return;
                    try {
                        const auto message = base64url_decode(line.fields[1]);
                        fb2k::inMainThread([message] { show_status(message); });
                    } catch (...) {}
                });
        },
        [](async_state& state) {
            for (const auto& line : state.result.lines) {
                if (line.fields.size() >= 3 && line.fields[0] == "UPDATE_NOTICE") {
                    auto message = base64url_decode(line.fields[1]);
                    auto url = base64url_decode(line.fields[2]);
                    if (message.empty()) message = "音源提示有新版本。";
                    if (url.rfind("https://", 0) == 0 || url.rfind("http://", 0) == 0) {
                        message += "\n\n是否打开音源提供的更新地址？";
                        if (MessageBoxW(core_api::get_main_window(), pfc::stringcvt::string_wide_from_utf8(message.c_str()),
                            L"LX 音源更新", MB_YESNO | MB_ICONINFORMATION | MB_DEFBUTTON1) == IDYES)
                            ShellExecuteW(core_api::get_main_window(), L"open",
                                pfc::stringcvt::string_wide_from_utf8(url.c_str()), nullptr, nullptr, SW_SHOWNORMAL);
                        else show_status("音源有可用更新");
                    } else {
                        MessageBoxW(core_api::get_main_window(), pfc::stringcvt::string_wide_from_utf8(message.c_str()),
                            L"LX 音源更新", MB_OK | MB_ICONINFORMATION);
                    }
                    return;
                }
            }
            auto message = bridge_message(state.result);
            if (message.empty()) throw std::runtime_error("未能取得音源更新结果");
            MessageBoxW(core_api::get_main_window(), pfc::stringcvt::string_wide_from_utf8(message.c_str()),
                L"音源更新完成", MB_OK | MB_ICONINFORMATION);
            command_manage_sources();
        }, true);
}

std::string write_payload_file(const std::vector<std::string>& payloads) {
    wchar_t directory[MAX_PATH + 1]{};
    wchar_t path[MAX_PATH + 1]{};
    if (!GetTempPathW(MAX_PATH, directory) || !GetTempFileNameW(directory, L"flx", 0, path))
        throw std::runtime_error("无法创建批量下载任务文件");
    HANDLE raw = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (raw == INVALID_HANDLE_VALUE) throw std::runtime_error("无法写入批量下载任务文件");
    std::string body;
    for (const auto& item : payloads) { body += item; body += "\r\n"; }
    DWORD written = 0;
    const BOOL ok = WriteFile(raw, body.data(), static_cast<DWORD>(body.size()), &written, nullptr);
    CloseHandle(raw);
    if (!ok || written != body.size()) {
        DeleteFileW(path);
        throw std::runtime_error("无法写入完整的批量下载任务");
    }
    pfc::stringcvt::string_utf8_from_wide converted(path);
    return converted.get_ptr();
}

void delete_utf8_file(const std::string& path) {
    if (path.empty()) return;
    pfc::stringcvt::string_wide_from_utf8 converted(path.c_str());
    DeleteFileW(converted.get_ptr());
}

uint64_t unsigned_field(const BridgeLine& line, size_t index) {
    if (index >= line.fields.size()) return 0;
    try { return std::stoull(line.fields[index]); }
    catch (...) { return 0; }
}

void update_component_progress(threaded_process_status& status, const BridgeLine& line) {
    if (line.fields.empty()) return;
    if (line.fields[0] == "STATUS" && line.fields.size() >= 2) {
        try { status.set_item(base64url_decode(line.fields[1]).c_str()); } catch (...) {}
    }
    else if (line.fields[0] == "PROGRESS" && line.fields.size() >= 3) {
        const auto received = unsigned_field(line, 1);
        const auto total = unsigned_field(line, 2);
        if (total > 0) status.set_progress(
            static_cast<t_size>(std::min<uint64_t>(received, total)),
            static_cast<t_size>(total));
    }
}

void update_download_progress(threaded_process_status& status, const BridgeLine& line) {
    if (line.fields.empty()) return;
    if (line.fields[0] == "STATUS" && line.fields.size() >= 2) {
        try { status.set_item(base64url_decode(line.fields[1]).c_str()); } catch (...) {}
    } else if (line.fields[0] == "TRANSFER" && line.fields.size() >= 6) {
        const auto item = unsigned_field(line, 1);
        const auto count = unsigned_field(line, 2);
        const auto received = unsigned_field(line, 3);
        const auto total = unsigned_field(line, 4);
        if (count > 0) status.set_progress(static_cast<t_size>(item > 0 ? item - 1 : 0), static_cast<t_size>(count));
        if (total > 0) status.set_progress_secondary_float(std::min(1.0, static_cast<double>(received) / total));

        std::string label;
        try { label = base64url_decode(line.fields[5]); } catch (...) {}
        if (total > 0) {
            char amount[96]{};
            sprintf_s(amount, "  %.1f / %.1f MB", received / 1048576.0, total / 1048576.0);
            label += amount;
        }
        if (!label.empty()) status.set_item(label.c_str());
    } else if (line.fields[0] == "PROGRESS" && line.fields.size() >= 4) {
        const auto completed = unsigned_field(line, 1);
        const auto count = unsigned_field(line, 2);
        if (count > 0) status.set_progress(static_cast<t_size>(completed), static_cast<t_size>(count));
        status.set_progress_secondary(threaded_process_status::progress_max);
        try {
            const auto label = base64url_decode(line.fields[3]);
            if (!label.empty()) status.set_item(label.c_str());
        } catch (...) {}
    }
}

bool has_kind(metadb_handle_list_cref items, bool collections) {
    for (size_t index = 0; index < items.get_count(); ++index) {
        try {
            std::string encoded;
            Payload payload;
            if (uri_to_payload(items[index]->get_path(), encoded, &payload) &&
                (collections ? payload.is_collection() : payload.is_track())) return true;
        } catch (...) {}
    }
    return false;
}

bool has_albums(metadb_handle_list_cref items) {
    for (size_t index = 0; index < items.get_count(); ++index) {
        try {
            std::string encoded;
            Payload payload;
            if (uri_to_payload(items[index]->get_path(), encoded, &payload) && payload.get(pf_kind) == "album") return true;
        } catch (...) {}
    }
    return false;
}

bool is_ranking(const Payload& payload) {
    return payload.get(pf_kind) == "playlist" && payload.get(pf_collection_kind) == "Ranking";
}

bool has_rankings(metadb_handle_list_cref items) {
    for (size_t index = 0; index < items.get_count(); ++index) {
        try {
            std::string encoded;
            Payload payload;
            if (uri_to_payload(items[index]->get_path(), encoded, &payload) && is_ranking(payload)) return true;
        } catch (...) {}
    }
    return false;
}

std::string quality_label(const std::string& q) {
    if (q == "128k") return "128K";
    if (q == "320k") return "320K";
    if (q == "flac") return "FLAC";
    if (q == "flac24bit") return "Hi-Res / 24bit";
    return q;
}

// Rebuild a payload string with a different preferred quality
std::string rebuild_payload_with_quality(const std::string& encoded, const std::string& quality) {
    auto payload = decode_payload(encoded);
    payload.fields[pf_preferred_quality] = quality;
    return encode_payload(payload.fields);
}

// Strip LRC timestamps like [00:12.34], skip metadata headers, and normalize to one line per lyric with CRLF
std::string strip_lrc_timestamps(const std::string& raw) {
    std::string result;
    result.reserve(raw.size());
    std::istringstream stream(raw);
    std::string line;
    bool prev_empty = false;

    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();

        size_t start = 0;
        while (start < line.size() && (line[start] == ' ' || line[start] == '\t')) ++start;

        // Skip metadata headers like [ti:...], [ar:...], [al:...], [by:...], [offset:...], [length:...]
        if (start < line.size() && line[start] == '[') {
            size_t close = line.find(']', start);
            if (close != std::string::npos) {
                std::string tag = line.substr(start + 1, close - start - 1);
                static const char* headers[] = { "ti:", "ar:", "al:", "by:", "offset:", "length:", "re:", "ve:" };
                bool is_header = false;
                for (const char* h : headers) {
                    if (tag.rfind(h, 0) == 0) { is_header = true; break; }
                }
                if (is_header && close + 1 >= line.size()) {
                    continue;
                }
            }
        }

        // Strip all timestamp tags [mm:ss.xx] or <mm:ss.xx> anywhere in the line
        std::string cleaned;
        cleaned.reserve(line.size());
        for (size_t i = 0; i < line.size(); ) {
            if (line[i] == '[' || line[i] == '<') {
                char close_char = (line[i] == '[') ? ']' : '>';
                size_t j = line.find(close_char, i + 1);
                if (j != std::string::npos) {
                    std::string inside = line.substr(i + 1, j - i - 1);
                    bool is_time = false;
                    if (!inside.empty() && std::isdigit(static_cast<unsigned char>(inside[0]))) {
                        is_time = true;
                        for (char c : inside) {
                            if (!std::isdigit(static_cast<unsigned char>(c)) && c != ':' && c != '.') {
                                is_time = false;
                                break;
                            }
                        }
                    }
                    if (is_time) {
                        i = j + 1;
                        continue;
                    }
                }
            }
            cleaned.push_back(line[i]);
            ++i;
        }

        // Trim leading and trailing whitespace from the cleaned line
        size_t first = cleaned.find_first_not_of(" \t");
        if (first == std::string::npos) {
            if (!result.empty() && !prev_empty) {
                result += "\r\n";
                prev_empty = true;
            }
            continue;
        }
        size_t last = cleaned.find_last_not_of(" \t");
        cleaned = cleaned.substr(first, last - first + 1);

        result += cleaned;
        result += "\r\n";
        prev_empty = false;
    }

    while (result.size() >= 2 && result.substr(result.size() - 2) == "\r\n") {
        result.resize(result.size() - 2);
    }
    return result;
}

} // namespace

void fetch_browser_search_page(std::vector<std::string>& items, SearchPage& result,
    const SearchPage& previous, int requested, bool last, abort_callback& abort) {
    async_state state;
    fetch_search_page(state, previous.options, requested, abort, &previous, last);
    items = std::move(state.items);
    result = std::move(state.page);
}

void command_search() {
    run_task("读取音源",
        [](async_state& state, threaded_process_status&, abort_callback& abort) {
            state.result = run_bridge({ "source-options" }, abort);
        },
        [](async_state& state) {
            const auto sources = source_options(state.result);
            if (sources.empty()) {
                popup_message::g_show("请先在“文件 → LX 在线音乐 → 管理音源”导入脚本。", "LX 在线音乐");
                return;
            }
            SearchOptions options;
            if (!show_search_dialog(options, sources)) return;
            run_task("LX 在线音乐搜索",
                [options](async_state& search, threaded_process_status&, abort_callback& abort) {
                    fetch_search_page(search, options, 1, abort);
                },
                [](async_state& search) { show_new_search(search); }, true);
        });
}

void command_browse() {
    run_task("读取音源",
        [](async_state& state, threaded_process_status&, abort_callback& abort) {
            state.result = run_bridge({ "source-options" }, abort);
        },
        [](async_state& state) {
            const auto sources = source_options(state.result);
            const auto available = std::any_of(sources.begin(), sources.end(), [](const SourceOption& source) {
                return std::any_of(source.sites.begin(), source.sites.end(), [](const SourceSite& site) {
                    return site.searchable && site.discover;
                });
            });
            if (!available) {
                popup_message::g_show("当前音源没有可用于随便看看的站点。", "LX 在线音乐");
                return;
            }
            SearchOptions options;
            if (!show_browse_dialog(options, sources)) return;
            run_task("LX 在线音乐随便看看",
                [options](async_state& browse, threaded_process_status&, abort_callback& abort) {
                    fetch_search_page(browse, options, 1, abort);
                },
                [](async_state& browse) { show_new_search(browse); }, true);
        });
}

void command_search_page(t_size playlist, t_size item) {
    try {
        auto playlists = playlist_manager::get();
        metadb_handle_ptr control;
        if (!playlists->playlist_get_item_handle(control, playlist, item)) return;
        std::string encoded;
        Payload payload;
        if (!uri_to_payload(control->get_path(), encoded, &payload) || !payload.is_navigation()) return;
        const auto page = decode_search_state(payload.get(pf_music_info_json));
        g_page_requests.erase(std::remove_if(g_page_requests.begin(), g_page_requests.end(), [](const auto& entry) { return entry.expired(); }), g_page_requests.end());
        for (const auto& entry : g_page_requests)
            if (const auto pending = entry.lock()) if (pending->position.m_playlist == playlist) return;
        int requested = page.page;
        const bool last = payload.get(pf_id) == "last";
        if (last) {
            if (page.total_pages < 1) throw std::runtime_error("音源没有返回总页数");
            requested = page.total_pages;
        } else if (payload.get(pf_id) == "jump") {
            if (!show_page_dialog(page, requested)) return;
        } else if (!parse_search_page_number(payload.get(pf_id), page.total_pages, requested))
            throw std::runtime_error("页码无效");
        if (requested == page.page) return;
        auto request = std::make_shared<page_request>();
        request->position.m_playlist = playlist;
        request->position.m_item = item;
        request->control = control;
        request->original_state = payload.get(pf_music_info_json);
        request->owned = owns_search_page(playlist, request->original_state);
        g_page_requests.push_back(request);
        run_task("搜索结果翻页",
            [page, requested, last](async_state& state, threaded_process_status&, abort_callback& abort) {
                fetch_search_page(state, page.options, requested, abort, &page, last);
            },
            [request](async_state& state) {
                auto playlists = playlist_manager::get();
                const auto target = request->position.m_playlist;
                const auto row = request->position.m_item;
                metadb_handle_ptr current;
                if (target == pfc_infinite || row == pfc_infinite ||
                    !playlists->playlist_get_item_handle(current, target, row) || current != request->control) return;
                if (!request->owned) { show_new_search(state); return; }
                if (!owns_search_page(target, request->original_state)) return;
                replace_playlist_payloads(state.items, target, playlists->get_active_playlist() == target);
                mark_search_page(target, state.page);
                if (state.page.options.type != "discover") remember_search(state.page.options);
            }, true);
    } catch (const std::exception& ex) { popup_message::g_complain("搜索结果翻页失败", ex.what()); }
}

namespace {

bool active_search_page(t_size& playlist, SearchPage& page) {
    playlist = playlist_manager::get()->get_active_playlist();
    if (playlist == pfc_infinite) return false;
    pfc::array_t<uint8_t> property;
    if (!playlist_manager_v2::get()->playlist_get_property(playlist, guid_search_page, property) ||
        property.get_size() == 0 || property.get_size() > 16384) return false;
    try {
        page = decode_search_state(std::string(
            reinterpret_cast<const char*>(property.get_ptr()), property.get_size()));
        return true;
    } catch (...) {
        return false;
    }
}

t_size find_page_action(t_size playlist, const std::string& action) {
    auto playlists = playlist_manager::get();
    const auto count = playlists->playlist_get_item_count(playlist);
    for (t_size row = 0; row < count; ++row) {
        metadb_handle_ptr item;
        std::string encoded;
        Payload payload;
        if (playlists->playlist_get_item_handle(item, playlist, row) &&
            uri_to_payload(item->get_path(), encoded, &payload) &&
            payload.is_navigation() && payload.get(pf_id) == action) return row;
    }
    return pfc_infinite;
}

void run_page_action(const std::string& action) {
    t_size playlist = pfc_infinite;
    SearchPage page;
    if (!active_search_page(playlist, page)) {
        show_status("当前播放列表不是 LX 搜索结果");
        return;
    }
    const auto row = find_page_action(playlist, action);
    if (row == pfc_infinite) {
        show_status("当前页没有这个翻页操作");
        return;
    }
    command_search_page(playlist, row);
}

} // namespace

void command_page_previous() {
    if (collection_browser_page_action("previous")) return;
    t_size playlist;
    SearchPage page;
    if (!active_search_page(playlist, page)) {
        show_status("当前播放列表不是 LX 搜索结果");
        return;
    }
    if (page.page <= 1) {
        show_status("已经是第一页");
        return;
    }
    run_page_action(std::to_string(page.page - 1));
}

void command_page_next() {
    if (collection_browser_page_action("next")) return;
    t_size playlist;
    SearchPage page;
    if (!active_search_page(playlist, page)) {
        show_status("当前播放列表不是 LX 搜索结果");
        return;
    }
    if (!page.has_next) {
        show_status("已经是最后一页");
        return;
    }
    run_page_action(std::to_string(page.page + 1));
}

void command_page_last() {
    if (collection_browser_page_action("last")) return;
    t_size playlist;
    SearchPage page;
    if (!active_search_page(playlist, page)) {
        show_status("当前播放列表不是 LX 搜索结果");
        return;
    }
    if (page.total_pages <= page.page) {
        show_status("已经是最后一页");
        return;
    }
    run_page_action("last");
}

void command_page_jump() {
    if (!collection_browser_page_action("jump")) run_page_action("jump");
}

void command_manage_sources() {
    run_task("管理音源",
        [](async_state& state, threaded_process_status&, abort_callback& abort) { state.result = run_bridge({ "source-options" }, abort); },
        [](async_state& state) {
            const auto sources = source_options(state.result);
            std::string selected;
            const auto action = show_source_manager(sources, selected);
            if (action == SourceAction::close) return;
            std::vector<std::string> args;
            if (action == SourceAction::select) {
                args = { "select-source", selected };
            } else if (action == SourceAction::remove) {
                if (MessageBoxW(core_api::get_main_window(), L"删除选中的音源？其他音源会保留。", L"管理音源",
                    MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2) != IDYES) { command_manage_sources(); return; }
                args = { "remove-source", selected };
            } else if (action == SourceAction::edit || action == SourceAction::update) {
                const auto found = std::find_if(sources.begin(), sources.end(),
                    [&selected](const SourceOption& source) { return source.id == selected; });
                if (found == sources.end()) return;
                if (action == SourceAction::edit) edit_source(*found);
                else update_source(*found);
                return;
            } else {
                if (action == SourceAction::import_file) {
                    pfc::string8 path;
                    if (!uGetOpenFileName(core_api::get_main_window(), "JavaScript 音源脚本|*.js|所有文件|*.*", 0,
                        "js", "选择 LX 音源脚本", nullptr, path, FALSE)) { command_manage_sources(); return; }
                    args = { "import-file", base64url_encode_text(path.c_str()) };
                } else {
                    std::string url;
                    if (!show_url_dialog(url)) { command_manage_sources(); return; }
                    args = { "import-url", base64url_encode_text(url) };
                }
            }
            start_bridge_message("管理音源", std::move(args), command_manage_sources);
        });
}

void start_update_checks() {
    if (g_update_timer) KillTimer(nullptr, g_update_timer);
    g_update_timer = 0;
    if (!g_updates_stopping && automatic_updates_enabled()) g_update_timer = SetTimer(nullptr, 0, 15000, update_timer);
}

void stop_update_checks() {
    g_updates_stopping = true;
    if (g_update_timer) KillTimer(nullptr, g_update_timer);
    g_update_timer = 0;
}

void command_check_update(bool automatic) {
    if (g_updates_stopping || (automatic && !automatic_updates_enabled())) return;
    if (g_update_pending.exchange(true)) {
        if (!automatic) show_status("正在检查或下载 LX 更新，请稍候");
        return;
    }
    const auto pending = std::make_shared<update_guard>();
    run_task("检查更新",
        [pending](async_state& state, threaded_process_status&, abort_callback& abort) {
            state.result = run_bridge({ "check-update", kVersion, sizeof(void*) == 8 ? "x64" : "x86" }, abort);
        },
        [pending, automatic](async_state& state) {
            if (g_updates_stopping) return;
            g_last_update_check = std::to_string(std::time(nullptr)).c_str();
            if (automatic && !automatic_updates_enabled()) return;
            std::string changelog;
            for (const auto& line : state.result.lines) {
                if (line.fields.size() >= 2 && line.fields[0] == "CHANGELOG") {
                    try { changelog = base64url_decode(line.fields[1]); } catch (...) {}
                    break;
                }
            }
            for (const auto& line : state.result.lines) {
                if (line.fields.size() >= 4 && line.fields[0] == "UPDATE") {
                    if (!show_update_dialog(line.fields[2], changelog)) return;
                    std::vector<wchar_t> host(32768);
                    const auto length = GetModuleFileNameW(nullptr, host.data(), static_cast<DWORD>(host.size()));
                    if (length == 0 || length >= host.size()) throw std::runtime_error("无法获取播放器路径");
                    const std::string host_path = pfc::stringcvt::string_utf8_from_wide(host.data());
                    const auto metadata = line.fields[3];
                    const auto target = base64url_encode_text(component_directory());
                    const auto executable = base64url_encode_text(host_path);
                    const auto parent = std::to_string(GetCurrentProcessId());
                    auto download = std::make_shared<async_state>();
                    auto task = threaded_process_callback_lambda::create(
                        threaded_process_callback_lambda::on_init_t{},
                        [download, metadata, target, executable, parent](threaded_process_status& status, abort_callback& abort) {
                            status.set_item("正在读取更新任务…");
                            status.set_progress(0, 1000);
                            try {
                                download->result = run_bridge({ "prepare-update", metadata, target, executable, parent }, abort,
                                    [&status](const BridgeLine& line) { update_component_progress(status, line); });
                            }
                            catch (const exception_aborted&) {
                                throw;
                            }
                            catch (const std::exception& ex) {
                                download->error = ex.what();
                            }
                            catch (...) {
                                download->error = "下载更新时发生未知错误";
                            }
                        },
                        [download, pending](threaded_process_callback::ctx_t, bool aborted) {
                            if (aborted) return;
                            if (!download->error.empty()) {
                                popup_message::g_complain("LX 组件更新失败", download->error.c_str());
                                return;
                            }
                            for (const auto& record : download->result.lines) {
                                if (record.fields.size() != 2 || record.fields[0] != "READY") continue;
                                if (!standard_commands::main_exit()) {
                                    const auto cancel = base64url_decode(record.fields[1]) + "\\cancel";
                                    const auto marker = CreateFileW(pfc::stringcvt::string_wide_from_utf8(cancel.c_str()),
                                        GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
                                    if (marker != INVALID_HANDLE_VALUE) CloseHandle(marker);
                                    popup_message::g_complain("LX 组件更新失败", "无法退出播放器，本次更新未安装");
                                }
                                return;
                            }
                            popup_message::g_complain("LX 组件更新失败", "更新助手未就绪，原版本未改动");
                        });
                    threaded_process::g_run_modal(task,
                        threaded_process::flag_show_abort | threaded_process::flag_show_progress |
                        threaded_process::flag_show_item, core_api::get_main_window(), "LX 组件更新");
                    return;
                }
            }
            if (!automatic) {
                const auto message = bridge_message(state.result);
                const auto latest = message.empty() ? std::string("当前已是最新版本") : message;
                MessageBoxW(core_api::get_main_window(),
                    pfc::stringcvt::string_wide_from_utf8(latest.c_str()),
                    L"LX 组件更新", MB_OK | MB_ICONINFORMATION);
            }
        }, false, automatic);
}

void command_copy_link(metadb_handle_list_cref items) {
    if (items.get_count() != 1) return;
    std::string encoded;
    Payload payload;
    if (!uri_to_payload(items[0]->get_path(), encoded, &payload) || payload.get(pf_kind) != "track") return;
    auto quality = payload.get(pf_preferred_quality);
    if (quality.empty()) quality = "flac";
    run_task("复制播放直链",
        [encoded, quality](async_state& state, threaded_process_status&, abort_callback& abort) {
            state.result = run_bridge({ "resolve", encoded, quality }, abort);
        },
        [](async_state& state) {
            std::string url;
            for (const auto& line : state.result.lines)
                if (line.fields.size() >= 2 && line.fields[0] == "URL") { url = base64url_decode(line.fields[1]); break; }
            if ((url.rfind("https://", 0) != 0 && url.rfind("http://", 0) != 0) ||
                url.size() > 65536 || url.find('\0') != std::string::npos)
                throw std::runtime_error("音源未返回有效的播放直链");
            copy_text_to_clipboard(url);
            show_status("已复制播放直链");
        });
}

void command_import_file() {
    pfc::string8 path;
    if (!uGetOpenFileName(core_api::get_main_window(), "JavaScript 音源脚本|*.js|所有文件|*.*", 0,
        "js", "选择 LX 音源脚本", nullptr, path, FALSE)) return;
    start_bridge_message("导入 LX 音源", { "import-file", base64url_encode_text(path.c_str()) });
}

void command_import_url() {
    std::string url;
    if (!show_url_dialog(url)) return;
    start_bridge_message("从网址导入 LX 音源", { "import-url", base64url_encode_text(url) });
}

void command_remove_source() {
    if (MessageBoxW(core_api::get_main_window(), L"确定删除当前导入的 LX 音源吗？", L"LX 在线音乐",
        MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2) != IDYES) return;
    start_bridge_message("删除 LX 音源", { "remove-source" });
}

void command_source_status() {
    start_bridge_message("检查 LX 音源", { "status" });
}

void command_lyrics(metadb_handle_list_cref items) {
    if (items.get_count() == 0) {
        popup_message::g_show("请先在播放列表中选择一首歌曲。", "LX 在线歌词");
        return;
    }
    std::string payload;
    std::string title;
    try {
        payload = lyric_payload_for_handle(items[0]);
        title = decode_payload(payload).get(pf_title);
    } catch (const std::exception& ex) {
        popup_message::g_complain("无法读取所选歌曲信息", ex);
        return;
    }
    run_task("获取在线歌词",
        [payload](async_state& state, threaded_process_status& status, abort_callback& abort) {
            status.set_item("正在匹配网易云、QQ、酷狗及 LRCLIB 歌词…");
            state.result = run_bridge({ "lyrics", payload }, abort);
        },
        [title](async_state& state) {
            const auto raw_lyrics = bridge_message(state.result, "LYRICS");
            if (raw_lyrics.empty()) {
                popup_message::g_show("没有找到匹配的同步歌词。", "LX 在线歌词");
                return;
            }
            show_lyrics_dialog("在线歌词 - " + title, strip_lrc_timestamps(raw_lyrics));
        }, true);
}

void command_expand(metadb_handle_list_cref items) {
    struct request { std::string payload, quality, title; };
    std::vector<request> requests;
    for (size_t index = 0; index < items.get_count(); ++index) {
        try {
            std::string encoded;
            Payload payload;
            if (!uri_to_payload(items[index]->get_path(), encoded, &payload) || !payload.is_collection()) continue;
            const auto q = payload.get(pf_preferred_quality);
            requests.push_back({ encoded, q.empty() ? "flac" : q, payload.get(pf_title) });
        } catch (...) {}
    }
    if (requests.empty()) {
        popup_message::g_show("请选择专辑、歌手或歌单搜索结果。", "LX 在线音乐");
        return;
    }
    const auto playlist = requests.size() == 1 ? "LX " + requests[0].title : "LX 展开结果";
    const auto origin = capture_navigation_origin(items);
    run_task("展开 LX 搜索结果",
        [requests](async_state& state, threaded_process_status& status, abort_callback& abort) {
            for (size_t index = 0; index < requests.size(); ++index) {
                abort.check();
                status.set_progress(index, requests.size());
                status.set_item(requests[index].title.c_str());
                const auto result = run_bridge({ "expand", requests[index].payload,
                    requests[index].quality }, abort);
                const auto items = bridge_items(result);
                state.items.insert(state.items.end(), items.begin(), items.end());
            }
            if (state.items.empty()) throw std::runtime_error("所选集合没有返回歌曲");
        },
        [playlist, origin](async_state& state) {
            if (has_collection_results(state.items)) show_collection_browser(state.items, playlist, nullptr, origin);
            else add_expanded_payloads(state.items, playlist, origin);
        }, true);
}

void command_download(metadb_handle_list_cref items, const SearchPage* search_context, const DialogOrigin& origin) {
    const auto show_error = [origin](const char* title, const char* message) {
        ::MessageBoxW(origin.owner(), pfc::stringcvt::string_wide_from_utf8(message),
            pfc::stringcvt::string_wide_from_utf8(title), MB_OK | MB_ICONERROR);
        origin.restore();
    };
    SearchPage search;
    DownloadOptions options;
    if (search_context) {
        search = *search_context;
        options.can_all_pages = search.options.type == "album" || search.options.type == "track";
    } else options.can_all_pages = download_search_context(items, search);
    std::vector<std::string> payloads;
    for (size_t index = 0; index < items.get_count(); ++index) {
        try {
            std::string encoded;
            Payload payload;
            if (uri_to_payload(items[index]->get_path(), encoded, &payload) &&
                (payload.is_track() || payload.get(pf_kind) == "album" || is_ranking(payload))) payloads.push_back(encoded);
        } catch (...) {}
    }
    if (payloads.empty() && !options.can_all_pages) {
        show_error("LX 批量下载", "请选择 LX 在线歌曲、专辑或榜单搜索结果。");
        return;
    }

    std::string last_folder, quality, naming_template;
    options.all_pages = payloads.empty();
    const bool accepted = show_download_dialog(payloads, last_folder, quality, naming_template, options, origin.owner());
    origin.restore();
    if (!accepted) return;
    std::string request_file;
    try { if (!options.all_pages) request_file = write_payload_file(payloads); }
    catch (const std::exception& ex) { show_error("无法创建下载任务", ex.what()); return; }
    const auto output = last_folder;

    auto state = std::make_shared<async_state>();
    auto task = threaded_process_callback_lambda::create(
        threaded_process_callback_lambda::on_init_t{},
        [state, request_file, output, quality, naming_template, options, search](threaded_process_status& status, abort_callback& abort) {
            status.set_item("正在读取下载项目…");
            status.set_progress(0);
            status.set_progress_secondary(0);
            try {
                const auto skip = options.skip_failures ? "1" : "0";
                const auto cleanup = options.cleanup_duplicates ? "1" : "0";
                const std::vector<std::string> args = options.all_pages
                    ? std::vector<std::string>{ "download-search", base64url_encode_text(output), quality, base64url_encode_text(naming_template),
                        skip, cleanup, search.options.source, search.options.site, search.options.type,
                        base64url_encode_text(search.options.keyword), std::to_string(search.total_pages), std::to_string(search.limit), std::to_string(options.workers) }
                    : std::vector<std::string>{ "download-batch", base64url_encode_text(request_file),
                        base64url_encode_text(output), quality, base64url_encode_text(naming_template), skip, cleanup, std::to_string(options.workers) };
                state->result = run_bridge(args, abort,
                    [&status](const BridgeLine& line) { update_download_progress(status, line); });
            } catch (const exception_aborted&) {
                delete_utf8_file(request_file);
                throw;
            } catch (const std::exception& ex) {
                state->error = ex.what();
            } catch (...) {
                state->error = "批量下载发生未知错误";
            }
            delete_utf8_file(request_file);
        },
        [state, request_file, output, origin](threaded_process_callback::ctx_t, bool was_aborted) {
            delete_utf8_file(request_file);
            origin.after_task([state, output, was_aborted](HWND owner) {
                if (was_aborted) return;
                if (!state->error.empty()) {
                    ::MessageBoxW(owner, pfc::stringcvt::string_wide_from_utf8(state->error.c_str()),
                        L"LX 批量下载失败", MB_OK | MB_ICONERROR);
                    return;
                }
                auto message = bridge_message(state->result, "DONE");
                if (message.empty()) message = "下载任务完成。";
                show_download_complete(message, output, owner);
            });
        });
    try {
        // Keep the long-running task independent of the browser's lifetime.
        // Its result dialog and focus return use the initiating browser instead.
        threaded_process::g_run_modeless(task,
            threaded_process::flag_show_abort | threaded_process::flag_show_progress_dual |
            threaded_process::flag_show_item | threaded_process::flag_no_focus,
            core_api::get_main_window(), "LX 批量下载");
    } catch (const std::exception& ex) {
        delete_utf8_file(request_file);
        show_error("无法启动批量下载", ex.what());
    }
}

void command_switch_quality(metadb_handle_list_cref items, const std::string& quality) {
    if (items.get_count() == 0) return;

    // Find the currently playing item to see if we need to resume playback
    auto pc = playback_control::get();
    metadb_handle_ptr now_playing;
    const bool is_playing = pc->get_now_playing(now_playing);
    double resume_position = 0.0;
    const bool resume_paused = is_playing && pc->is_paused();
    metadb_handle_ptr resume_handle;
    t_size resume_playlist_index = pfc_infinite;

    auto playlists = playlist_manager::get();
    const auto active = playlists->get_active_playlist();
    if (active == pfc_infinite) return;

    bool undo_created = false;

    for (size_t i = 0; i < items.get_count(); ++i) {
        try {
            std::string encoded;
            Payload payload;
            if (!uri_to_payload(items[i]->get_path(), encoded, &payload) || !payload.is_track()) continue;
            const auto current_quality = payload.get(pf_preferred_quality);
            if (current_quality == quality) continue; // already that quality

            const auto new_encoded = rebuild_payload_with_quality(encoded, quality);
            const auto new_uri = payload_to_uri(new_encoded);

            // Find this item's position in the active playlist
            const auto count = playlists->playlist_get_item_count(active);
            for (size_t pi = 0; pi < count; ++pi) {
                metadb_handle_ptr h;
                if (!playlists->playlist_get_item_handle(h, active, pi)) continue;
                if (h == items[i]) {
                    // Check if this is the currently playing track
                    if (is_playing && now_playing.is_valid() && now_playing == items[i]) {
                        resume_position = pc->playback_get_position();
                    }

                    // Create new handle with the new URI
                    auto new_handle = metadb::get()->handle_create(new_uri.c_str(), 0);
                    auto new_payload = decode_payload(new_encoded);
                    size_t track_number = 0, track_total = 0;
                    for (size_t position = 0; position < count; ++position) {
                        metadb_handle_ptr candidate;
                        std::string candidate_encoded;
                        Payload candidate_payload;
                        if (playlists->playlist_get_item_handle(candidate, active, position) &&
                            uri_to_payload(candidate->get_path(), candidate_encoded, &candidate_payload) &&
                            candidate_payload.is_track()) {
                            ++track_total;
                            if (position <= pi) ++track_number;
                        }
                    }
                    hint_payload_info(new_handle, new_payload, std::max<size_t>(1, track_number),
                        std::max<size_t>(track_number, track_total));

                    // Replace: remove old, insert new at same position
                    if (!undo_created) {
                        playlists->playlist_undo_backup(active);
                        undo_created = true;
                    }
                    pfc::bit_array_one remove_mask(pi);
                    playlists->playlist_remove_items(active, remove_mask);
                    metadb_handle_list new_list;
                    new_list.add_item(new_handle);
                    pfc::bit_array_true sel_mask;
                    playlists->playlist_insert_items(active, pi, new_list, sel_mask);
                    playlists->playlist_set_focus_item(active, pi);

                    if (is_playing && now_playing.is_valid() && now_playing == items[i]) {
                        resume_handle = new_handle;
                        resume_playlist_index = pi;
                    }
                    break;
                }
            }
        } catch (...) {}
    }

    if (resume_handle.is_valid() && resume_playlist_index != pfc_infinite) {
        // Wait for the replacement stream to be fully initialized before seeking.
        // This is armed before playback so the new-track callback cannot be missed.
        arm_quality_resume(resume_handle, resume_position, resume_paused);
        playlists->playlist_execute_default_action(active, resume_playlist_index);
    }

}

// ---------------------------------------------------------------- main menu

static const GUID guid_main_group = { 0x418263d2, 0x8a92, 0x493f,{ 0xa2, 0x2d, 0x4c, 0xc4, 0xaa, 0x28, 0xe8, 0x41 } };
static const GUID guid_search = { 0xd69760c3, 0x7c12, 0x4cd9,{ 0xbd, 0x24, 0xa0, 0xc0, 0x46, 0x91, 0xf7, 0x8e } };
static const GUID guid_browse = { 0xd69760c4, 0x7c12, 0x4cd9,{ 0xbd, 0x24, 0xa0, 0xc0, 0x46, 0x91, 0xf7, 0x8f } };
static const GUID guid_import_file = { 0x46b1f808, 0xee91, 0x4418,{ 0x8c, 0x2b, 0xc5, 0x4d, 0xe3, 0x5f, 0xc5, 0x9f } };
static const GUID guid_import_url = { 0xc5ad0577, 0x594f, 0x4b75,{ 0x8c, 0xf4, 0xeb, 0xd4, 0x15, 0xd8, 0xe8, 0x93 } };
static const GUID guid_status = { 0xd745217e, 0x38c1, 0x4b35,{ 0xa2, 0x3c, 0x91, 0x61, 0xf2, 0x5f, 0xbb, 0x8f } };
static const GUID guid_remove = { 0x37dd5e63, 0x2860, 0x4a60,{ 0x93, 0x34, 0xe6, 0x19, 0x5e, 0xe4, 0x7c, 0xb4 } };
static const GUID guid_lyrics_main = { 0x85d64b42, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_manage = { 0x85d64b43, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_update = { 0x85d64b44, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
const GUID guid_back = { 0x85d64b45, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_settings = { 0x85d64b48, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_page_previous = { 0x85d64b4a, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_page_next = { 0x85d64b4b, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_page_last = { 0x85d64b4c, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_page_jump = { 0x85d64b4d, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };
static const GUID guid_release_page = { 0x85d64b4e, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };

static mainmenu_group_popup_factory g_main_group(guid_main_group, mainmenu_groups::file,
    mainmenu_commands::sort_priority_dontcare, "LX 在线音乐");

class main_commands : public mainmenu_commands {
public:
    enum { search, browse, page_previous, page_next, page_last, page_jump, manage, update, release_page, lyrics, back, settings, count };
    t_uint32 get_command_count() override { return count; }
    GUID get_command(t_uint32 index) override {
        static const GUID values[] = { guid_search, guid_browse, guid_page_previous, guid_page_next,
            guid_page_last, guid_page_jump, guid_manage, guid_update, guid_release_page,
            guid_lyrics_main, guid_back, guid_settings };
        return index < count ? values[index] : pfc::guid_null;
    }
    void get_name(t_uint32 index, pfc::string_base& out) override {
        static const char* values[] = { "搜索在线音乐…", "随便看看…", "上一页", "下一页",
            "跳到尾页", "页数跳转…", "管理音源…", "检查更新", "发布页",
            "获取所选歌曲歌词", "返回上一层", "设置…" };
        if (index < count) out = values[index];
    }
    bool get_description(t_uint32 index, pfc::string_base& out) override {
        if (index == browse) out = "选择音源和站点后浏览榜单或公开推荐，并直接显示在新的播放列表中。";
        else if (index == page_previous) out = "切换到当前 LX 搜索结果的上一页。可在快捷键设置中绑定按键。";
        else if (index == page_next) out = "切换到当前 LX 搜索结果的下一页。可在快捷键设置中绑定按键。";
        else if (index == page_last) out = "切换到当前 LX 搜索结果能够访问的尾页。可在快捷键设置中绑定按键。";
        else if (index == page_jump) out = "输入页码并跳转当前 LX 搜索结果。可在快捷键设置中绑定按键。";
        else if (index == release_page) out = "用默认浏览器打开 LX 在线音乐 GitHub 发布页。";
        else if (index == lyrics) out = "获取播放列表中所选歌曲的在线歌词；可在 foobar2000 键盘快捷键设置中绑定 Shift+Enter。";
        else if (index == back) out = "返回打开集合前的播放列表。默认退格键；在快捷键设置中绑定其他按键后，默认退格键自动停用。";
        else out = "LX 在线音乐搜索与音源管理。";
        return index < count;
    }
    bool get_display(t_uint32 index, pfc::string_base& out, t_uint32& flags) override {
        if (index == page_previous || index == page_next || index == page_last ||
            index == page_jump || index == lyrics || index == back) return false;
        return mainmenu_commands::get_display(index, out, flags);
    }
    GUID get_parent() override { return guid_main_group; }
    void execute(t_uint32 index, service_ptr_t<service_base>) override {
        switch (index) {
        case search: command_search(); break;
        case browse: command_browse(); break;
        case page_previous: command_page_previous(); break;
        case page_next: command_page_next(); break;
        case page_last: command_page_last(); break;
        case page_jump: command_page_jump(); break;
        case manage: command_manage_sources(); break;
        case update: command_check_update(); break;
        case release_page: {
            const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(core_api::get_main_window(), L"open",
                L"https://github.com/lumiliya2009/foo_lxmusic/releases", nullptr, nullptr, SW_SHOWNORMAL));
            if (result <= 32) show_status("无法打开 GitHub 发布页");
            break;
        }
        case settings: show_component_preferences(); break;
        case back: command_back(); break;
        case lyrics: { auto items = selected_playlist_items(); command_lyrics(items); break; }
        default: break;
        }
    }
};
static mainmenu_commands_factory_t<main_commands> g_main_commands;

// ---------------------------------------------------------------- context menu

static const GUID guid_context_group = { 0xc13d6042, 0xee98, 0x4912,{ 0xa1, 0x3c, 0xc3, 0xdb, 0xb2, 0xa5, 0x27, 0x72 } };
static const GUID guid_context_lyrics = { 0xb0334ebc, 0x0c2e, 0x4854,{ 0xb9, 0x0d, 0x61, 0x54, 0xce, 0x91, 0x3a, 0x38 } };
static const GUID guid_context_expand = { 0x4407be4b, 0x76aa, 0x46bb,{ 0x95, 0xc3, 0x61, 0xec, 0x0f, 0x17, 0xb4, 0xb0 } };
static const GUID guid_context_download = { 0x9f74694f, 0xe3b6, 0x468c,{ 0x90, 0x63, 0x28, 0x1a, 0x7a, 0x16, 0x15, 0xc7 } };
static const GUID guid_context_quality_128k = { 0xa1e2c301, 0x5b4f, 0x4d8a,{ 0xb3, 0x12, 0x7e, 0xf4, 0x00, 0x9a, 0xc1, 0x01 } };
static const GUID guid_context_quality_320k = { 0xa1e2c302, 0x5b4f, 0x4d8a,{ 0xb3, 0x12, 0x7e, 0xf4, 0x00, 0x9a, 0xc1, 0x02 } };
static const GUID guid_context_quality_flac = { 0xa1e2c303, 0x5b4f, 0x4d8a,{ 0xb3, 0x12, 0x7e, 0xf4, 0x00, 0x9a, 0xc1, 0x03 } };
static const GUID guid_context_quality_flac24 = { 0xa1e2c304, 0x5b4f, 0x4d8a,{ 0xb3, 0x12, 0x7e, 0xf4, 0x00, 0x9a, 0xc1, 0x04 } };
static const GUID guid_context_copy_link = { 0x85d64b49, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };

static contextmenu_group_popup_factory g_context_group(guid_context_group, contextmenu_groups::root, "LX 在线音乐", 0);

class context_commands : public contextmenu_item_simple {
public:
    enum { lyrics, expand, download, quality_128k, quality_320k, quality_flac, quality_flac24, copy_link, count };
    GUID get_parent() override { return guid_context_group; }
    unsigned get_num_items() override { return count; }
    void get_item_name(unsigned index, pfc::string_base& out) override {
        static const char* values[] = {
            "获取歌词",
            "打开所选专辑 / 歌手 / 歌单",
            "批量下载所选歌曲…",
            "切换音质: 128K",
            "切换音质: 320K",
            "切换音质: FLAC",
            "切换音质: Hi-Res / 24bit",
            "复制播放直链"
        };
        if (index < count) out = values[index];
    }
    GUID get_item_guid(unsigned index) override {
        static const GUID values[] = {
            guid_context_lyrics, guid_context_expand, guid_context_download,
            guid_context_quality_128k, guid_context_quality_320k,
            guid_context_quality_flac, guid_context_quality_flac24, guid_context_copy_link
        };
        return index < count ? values[index] : pfc::guid_null;
    }
    bool get_item_description(unsigned index, pfc::string_base& out) override {
        static const char* values[] = {
            "显示所选歌曲的在线歌词。",
            "把集合中的歌曲加入新的 foobar2000 播放列表。",
            "下载所选歌曲、整张专辑或整个榜单；专辑自动建立文件夹并保存封面。",
            "切换所选歌曲为 128K 音质并续播。",
            "切换所选歌曲为 320K 音质并续播。",
            "切换所选歌曲为 FLAC 音质并续播。",
            "切换所选歌曲为 Hi-Res / 24bit 音质并续播。",
            "使用当前音源解析所选歌曲并复制播放地址。"
        };
        if (index >= count) return false;
        out = values[index]; return true;
    }
    void context_command(unsigned index, metadb_handle_list_cref data, const GUID&) override {
        if (index == lyrics) command_lyrics(data);
        else if (index == expand) command_expand(data);
        else if (index == download) command_download(data);
        else if (index == quality_128k) command_switch_quality(data, "128k");
        else if (index == quality_320k) command_switch_quality(data, "320k");
        else if (index == quality_flac) command_switch_quality(data, "flac");
        else if (index == quality_flac24) command_switch_quality(data, "flac24bit");
        else if (index == copy_link) command_copy_link(data);
    }
    bool context_get_display(unsigned index, metadb_handle_list_cref data, pfc::string_base& out,
        unsigned& flags, const GUID& caller) override {
        if (data.get_count() == 0) return false;
        if (index == copy_link && (data.get_count() != 1 || !has_kind(data, false))) return false;
        bool navigation_only = true;
        for (t_size i = 0; i < data.get_count(); ++i) {
            std::string encoded;
            Payload payload;
            try {
                if (!uri_to_payload(data[i]->get_path(), encoded, &payload) || !payload.is_navigation()) navigation_only = false;
            } catch (...) { navigation_only = false; }
        }
        if (navigation_only) {
            SearchPage page;
            if (index == download && download_search_context(data, page)) {
                out = "下载所有搜索结果页…";
                flags = 0;
                return true;
            }
            return false;
        }
        if (index == lyrics && has_kind(data, true) && !has_kind(data, false)) return false;
        if (index == expand && !has_kind(data, true)) return false;
        if (index == download && has_albums(data)) {
            out = has_kind(data, false) ? "下载所选歌曲和专辑（含封面）…" : "下载整张专辑（含封面）…";
            flags = 0;
            return true;
        }
        if (index == download && has_rankings(data)) {
            out = has_kind(data, false) ? "下载所选歌曲和榜单…" : "下载整个榜单…";
            flags = 0;
            return true;
        }
        if (index == download && !has_kind(data, false)) return false;
        // Quality switch items: only show for LX tracks
        if (index >= quality_128k && index <= quality_flac24 && !has_kind(data, false)) return false;
        return contextmenu_item_simple::context_get_display(index, data, out, flags, caller);
    }
};
static contextmenu_item_factory_t<context_commands> g_context_commands;

} // namespace lxmusic
