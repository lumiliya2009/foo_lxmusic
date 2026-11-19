#pragma once

namespace lxmusic {

constexpr const char* kSchemePrefix = "lxmusic://item/";
constexpr const char* kSchemeSuffix = ".lxmusic";
constexpr const char* kVersion = "0.1.7";

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
};

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

struct SourceSite { std::string id, label; bool collections = false, searchable = true; };
struct SourceOption {
    std::string id, name;
    bool active = false;
    std::vector<SourceSite> sites;
};
enum class SourceAction { close, import_file, import_url, remove, select };

std::string base64url_encode(const void* data, size_t bytes);
std::string base64url_encode_text(const std::string& text);
std::string base64url_decode(const std::string& text);
std::string encode_payload(const std::vector<std::string>& fields);
Payload decode_payload(const std::string& encoded);
std::string payload_to_uri(const std::string& encoded);
bool uri_to_payload(const char* path, std::string& encoded, Payload* decoded = nullptr);
void payload_to_info(const Payload& payload, file_info& info);
void hint_payload_info(metadb_handle_ptr handle, const Payload& payload);
void refresh_playlist_payload_info();
void install_collection_actions(t_size playlist);

std::string component_directory();
std::string data_directory();
BridgeResult run_bridge(const std::vector<std::string>& args, abort_callback& abort,
    BridgeLineCallback on_line = {});
BridgeResult run_bridge_noabort(const std::vector<std::string>& args);
std::vector<std::string> bridge_items(const BridgeResult& result);
std::string bridge_message(const BridgeResult& result, const char* record = "OK");
std::string bridge_error(const BridgeResult& result);
file::ptr open_audio_stream(const std::string& url, abort_callback& abort);

void add_payloads_to_playlist(const std::vector<std::string>& payloads, const std::string& playlist_name);
metadb_handle_list selected_playlist_items();
std::string payload_for_handle(metadb_handle_ptr handle);
std::string lyric_payload_for_handle(metadb_handle_ptr handle);

bool show_search_dialog(SearchOptions& options, const std::vector<SourceOption>& sources);
SourceAction show_source_manager(const std::vector<SourceOption>& sources, std::string& selected);
bool show_url_dialog(std::string& url);
void show_lyrics_dialog(const std::string& caption, const std::string& lyrics);
bool show_download_dialog(std::vector<std::string>& payloads, std::string& folder, std::string& quality);

void command_search();
void command_import_file();
void command_import_url();
void command_remove_source();
void command_source_status();
void command_manage_sources();
void command_check_update();
void command_lyrics(metadb_handle_list_cref items);
void command_expand(metadb_handle_list_cref items);
void command_download(metadb_handle_list_cref items);
void command_switch_quality(metadb_handle_list_cref items, const std::string& quality);

} // namespace lxmusic
