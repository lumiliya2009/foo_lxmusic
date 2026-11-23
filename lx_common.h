#pragma once

#include "dialog_origin.h"

namespace lxmusic {

constexpr const char* kSchemePrefix = "lxmusic://item/";
constexpr const char* kSchemeSuffix = ".lxmusic";
constexpr const char* kVersion = "0.1.17";

enum PayloadField : size_t {
    pf_version = 0,
    pf_kind,
    pf_platform_id,
    pf_lx_source_id,
    pf_platform_label,
    pf_id,
    pf_title,
    pf_artist,
    pf_album,
    pf_duration_ms,
    pf_artwork,
    pf_qualities,
    pf_music_info_json,
    pf_preferred_quality,
    pf_collection_kind,
    pf_count
};

struct Payload {
    std::vector<std::string> fields;

    const std::string& get(size_t index) const;
    bool is_track() const;
    bool is_collection() const;
    bool is_navigation() const;
};
bool is_ranking_payload(const Payload& payload);
bool has_collection_results(const std::vector<std::string>& items);
std::string collection_result_label(const Payload& payload);

struct SearchPlaylistPlacement {
    std::string name;
    bool create_new = true;
};
SearchPlaylistPlacement resolve_search_playlist_placement(const std::string& automatic_name,
    bool fixed_playlist_enabled, const std::string& fixed_playlist_name);
bool use_fixed_search_playlist();
std::string fixed_search_playlist_name();

struct BridgeLine {
    std::vector<std::string> fields;
};

struct BridgeResult {
    DWORD exit_code = 0;
    std::string raw;
    std::vector<BridgeLine> lines;
};

using BridgeLineCallback = std::function<void(const BridgeLine&)>;

struct SearchOptions {
    std::string source;
    std::string keyword;
    std::string site;
    std::string type;
    std::string quality;
};

struct SearchPage {
    SearchOptions options;
    int page = 1, limit = 30, total_pages = -1, total_items = -1;
    bool has_next = false;
};

std::string encode_search_state(const SearchPage& page);
SearchPage decode_search_state(const std::string& encoded);
SearchPage search_page_from_bridge(const BridgeResult& result, const SearchOptions& options, int requested);
std::string search_page_label(const SearchPage& page);
void append_search_controls(std::vector<std::string>& items, const SearchPage& page);
bool parse_search_page_number(const std::string& text, int maximum, int& page);
int find_last_search_page(int current, int advertised, const std::function<bool(int)>& has_items);
std::vector<SearchOptions> decode_search_history(const std::string& data);
std::string update_search_history(const std::string& data, const SearchOptions& options);
void remember_search(const SearchOptions& options);

struct SourceSite {
    std::string id, label;
    bool collections = false, searchable = true, featured = false, rankings = false, categories = false, discover = false;
};
struct SourceOption {
    std::string id, name, url;
    bool active = false;
    bool url_available = false;
    std::string details, revision;
    std::vector<SourceSite> sites;
};
enum class SourceAction { close, import_file, import_url, remove, select, edit, update };

std::string base64url_encode(const void* data, size_t bytes);
std::string base64url_encode_text(const std::string& text);
std::string base64url_decode(const std::string& text);
std::string encode_payload(const std::vector<std::string>& fields);
Payload decode_payload(const std::string& encoded);
std::string payload_to_uri(const std::string& encoded);
bool uri_to_payload(const char* path, std::string& encoded, Payload* decoded = nullptr);
void payload_to_info(const Payload& payload, file_info& info);
std::string format_track_display_title(const Payload& payload, const std::string& naming_template,
    size_t number = 1, size_t total = 1);
std::string download_naming_template();
bool apply_naming_to_search();
void payload_to_info(const Payload& payload, file_info& info, size_t number, size_t total);
void hint_payload_info(metadb_handle_ptr handle, const Payload& payload, size_t number = 1, size_t total = 1);
void refresh_playlist_payload_info();
void install_collection_actions(t_size playlist);

std::string component_directory();
std::string data_directory();
BridgeResult run_bridge(const std::vector<std::string>& args, abort_callback& abort,
    BridgeLineCallback on_line = {}, const std::string* standard_input = nullptr);
BridgeResult run_bridge_noabort(const std::vector<std::string>& args);
std::vector<std::string> bridge_items(const BridgeResult& result);
std::string bridge_message(const BridgeResult& result, const char* record = "OK");
std::string bridge_error(const BridgeResult& result);
void show_status(const std::string& message);
void copy_text_to_clipboard(const std::string& text);
file::ptr open_audio_stream(const std::string& url, abort_callback& abort);

t_size add_payloads_to_playlist(const std::vector<std::string>& payloads, const std::string& playlist_name,
    t_size preserve_playlist = pfc_infinite, bool create_new = false);
void replace_playlist_payloads(const std::vector<std::string>& payloads, t_size playlist, bool activate = true);
struct NavigationOrigin;
std::shared_ptr<NavigationOrigin> capture_navigation_origin(metadb_handle_list_cref items);
std::shared_ptr<NavigationOrigin> capture_browser_navigation(std::function<bool()> restore);
bool restore_navigation_origin(const std::shared_ptr<NavigationOrigin>& origin);
void add_expanded_payloads(const std::vector<std::string>& payloads, const std::string& playlist_name,
    const std::shared_ptr<NavigationOrigin>& origin);
void reset_playlist_navigation(t_size playlist);
bool command_back();
extern const GUID guid_back;
metadb_handle_list selected_playlist_items();
std::string payload_for_handle(metadb_handle_ptr handle);
std::string lyric_payload_for_handle(metadb_handle_ptr handle);

bool show_search_dialog(SearchOptions& options, const std::vector<SourceOption>& sources);
bool show_browse_dialog(SearchOptions& options, const std::vector<SourceOption>& sources);
bool show_page_dialog(const SearchPage& state, int& page, HWND owner = nullptr);
void show_collection_browser(const std::vector<std::string>& items, const std::string& title,
    const SearchPage* page = nullptr, const std::shared_ptr<NavigationOrigin>& origin = {});
bool collection_browser_back();
bool collection_browser_page_action(const std::string& action);
void fetch_browser_search_page(std::vector<std::string>& items, SearchPage& result,
    const SearchPage& previous, int requested, bool last, abort_callback& abort);
SourceAction show_source_manager(const std::vector<SourceOption>& sources, std::string& selected);
bool show_source_editor(const std::string& title, std::string& script, std::string& revision);
bool show_url_dialog(std::string& url);
bool show_qq_playlists_dialog(std::string& input);
bool show_update_dialog(const std::string& version, const std::string& changelog);
void show_lyrics_dialog(const std::string& caption, const std::string& lyrics);
void show_download_complete(const std::string& summary, const std::string& folder, HWND owner = nullptr);
struct DownloadOptions { bool can_all_pages = false, all_pages = false, skip_failures = true, cleanup_duplicates = false; int workers = 3; };
bool show_download_dialog(std::vector<std::string>& payloads, std::string& folder, std::string& quality,
    std::string& naming_template, DownloadOptions& options, HWND owner = nullptr);

void command_search();
void command_browse();
void command_qq_playlists();
void command_page_previous();
void command_page_next();
void command_page_last();
void command_page_jump();
void command_search_page(t_size playlist, t_size item);
void command_import_file();
void command_import_url();
void command_remove_source();
void command_source_status();
void command_manage_sources();
void command_check_update(bool automatic = false);
bool automatic_updates_enabled();
void show_component_preferences();
void start_update_checks();
void stop_update_checks();
void command_lyrics(metadb_handle_list_cref items);
void command_expand(metadb_handle_list_cref items);
void command_download(metadb_handle_list_cref items, const SearchPage* search_context = nullptr,
    const DialogOrigin& origin = {});
void command_switch_quality(metadb_handle_list_cref items, const std::string& quality);

} // namespace lxmusic
