#include "stdafx.h"
#include <charconv>
#include <cstring>

namespace lxmusic {

namespace {

class win_handle {
public:
    win_handle() = default;
    explicit win_handle(HANDLE value) : m_value(value) {}
    ~win_handle() { reset(); }
    win_handle(const win_handle&) = delete;
    win_handle& operator=(const win_handle&) = delete;
    win_handle(win_handle&& other) noexcept : m_value(other.release()) {}
    win_handle& operator=(win_handle&& other) noexcept {
        if (this != &other) reset(other.release());
        return *this;
    }
    HANDLE get() const { return m_value; }
    HANDLE release() { HANDLE value = m_value; m_value = nullptr; return value; }
    void reset(HANDLE value = nullptr) {
        if (m_value && m_value != INVALID_HANDLE_VALUE) CloseHandle(m_value);
        m_value = value;
    }
private:
    HANDLE m_value = nullptr;
};

std::wstring wide(const std::string& value) {
    pfc::stringcvt::string_wide_from_utf8 converted(value.c_str());
    return converted.get_ptr();
}

std::string trim_line(std::string value) {
    while (!value.empty() && (value.back() == '\r' || value.back() == '\n')) value.pop_back();
    return value;
}

std::vector<std::string> split(const std::string& value, char delimiter) {
    std::vector<std::string> result;
    size_t begin = 0;
    for (;;) {
        const auto end = value.find(delimiter, begin);
        result.emplace_back(value.substr(begin, end == std::string::npos ? end : end - begin));
        if (end == std::string::npos) break;
        begin = end + 1;
    }
    return result;
}

std::string title_field(metadb_handle_ptr handle, const char* expression) {
    pfc::string8 value;
    handle->format_title_legacy(nullptr, value, expression, nullptr);
    return value.c_str();
}

} // namespace

const std::string& Payload::get(size_t index) const {
    static const std::string empty;
    return index < fields.size() ? fields[index] : empty;
}

bool Payload::is_track() const { return get(pf_kind) == "track"; }
bool Payload::is_navigation() const { return get(pf_kind) == "search-page"; }
bool Payload::is_collection() const {
    const auto& kind = get(pf_kind);
    return kind == "album" || kind == "artist" || kind == "playlist";
}
bool is_ranking_payload(const Payload& payload) {
    return payload.is_collection() && payload.get(pf_collection_kind) == "Ranking";
}

std::string collection_result_label(const Payload& payload) {
    auto label = payload.get(pf_title);
    if ((payload.get(pf_kind) == "album" ||
        (payload.get(pf_kind) == "playlist" && !is_ranking_payload(payload))) &&
        !payload.get(pf_artist).empty())
        label += " — " + payload.get(pf_artist);
    return label;
}

SearchPlaylistPlacement resolve_search_playlist_placement(const std::string& automatic_name,
    bool fixed_playlist_enabled, const std::string& fixed_playlist_name) {
    SearchPlaylistPlacement result;
    auto fixed = fixed_playlist_name;
    const auto not_space = [](unsigned char c) { return c != ' ' && c != '\t' && c != '\r' && c != '\n'; };
    fixed.erase(fixed.begin(), std::find_if(fixed.begin(), fixed.end(), not_space));
    fixed.erase(std::find_if(fixed.rbegin(), fixed.rend(), not_space).base(), fixed.end());
    if (fixed_playlist_enabled && !fixed.empty()) {
        result.name = fixed;
        result.create_new = false;
    } else {
        result.name = automatic_name;
        result.create_new = true;
    }
    return result;
}

std::string base64url_encode(const void* data, size_t bytes) {
    static constexpr char table[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    const auto* input = static_cast<const unsigned char*>(data);
    std::string out;
    out.reserve((bytes * 4 + 2) / 3);
    for (size_t i = 0; i < bytes; i += 3) {
        const unsigned value = (static_cast<unsigned>(input[i]) << 16) |
            (i + 1 < bytes ? static_cast<unsigned>(input[i + 1]) << 8 : 0) |
            (i + 2 < bytes ? static_cast<unsigned>(input[i + 2]) : 0);
        out.push_back(table[(value >> 18) & 63]);
        out.push_back(table[(value >> 12) & 63]);
        if (i + 1 < bytes) out.push_back(table[(value >> 6) & 63]);
        if (i + 2 < bytes) out.push_back(table[value & 63]);
    }
    return out;
}

std::string base64url_encode_text(const std::string& text) {
    return base64url_encode(text.data(), text.size());
}

std::string base64url_decode(const std::string& text) {
    auto decode_char = [](unsigned char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '-' || c == '+') return 62;
        if (c == '_' || c == '/') return 63;
        return -1;
    };
    std::string out;
    out.reserve(text.size() * 3 / 4);
    unsigned value = 0;
    int bits = -8;
    for (unsigned char c : text) {
        if (c == '=') break;
        const int decoded = decode_char(c);
        if (decoded < 0) throw std::runtime_error("无效的 Base64URL 数据");
        value = (value << 6) | static_cast<unsigned>(decoded);
        bits += 6;
        if (bits >= 0) {
            out.push_back(static_cast<char>((value >> bits) & 0xFF));
            bits -= 8;
        }
    }
    return out;
}

std::string encode_payload(const std::vector<std::string>& fields) {
    if (fields.size() != pf_count) throw std::runtime_error("在线项目字段数不正确");
    std::string raw;
    for (const auto& field : fields) {
        raw += std::to_string(field.size());
        raw.push_back(':');
        raw += field;
    }
    return base64url_encode_text(raw);
}

Payload decode_payload(const std::string& encoded) {
    const auto raw = base64url_decode(encoded);
    Payload payload;
    size_t offset = 0;
    while (offset < raw.size()) {
        const auto colon = raw.find(':', offset);
        if (colon == std::string::npos || colon == offset) throw std::runtime_error("在线项目数据损坏");
        size_t count = 0;
        const auto first = raw.data() + offset;
        const auto last = raw.data() + colon;
        const auto parsed = std::from_chars(first, last, count);
        if (parsed.ec != std::errc() || parsed.ptr != last || colon + 1 + count > raw.size())
            throw std::runtime_error("在线项目数据损坏");
        offset = colon + 1;
        payload.fields.emplace_back(raw.substr(offset, count));
        offset += count;
    }
    if (payload.fields.size() != pf_count || payload.get(pf_version) != "1")
        throw std::runtime_error("在线项目版本不受支持");
    return payload;
}

bool has_collection_results(const std::vector<std::string>& items) {
    return std::any_of(items.begin(), items.end(), [](const std::string& encoded) {
        return decode_payload(encoded).is_collection();
    });
}

std::string payload_to_uri(const std::string& encoded) {
    return std::string(kSchemePrefix) + encoded + kSchemeSuffix;
}

bool uri_to_payload(const char* path, std::string& encoded, Payload* decoded) {
    if (!path) return false;
    const std::string value(path);
    if (value.size() <= strlen(kSchemePrefix) + strlen(kSchemeSuffix) ||
        _strnicmp(value.c_str(), kSchemePrefix, strlen(kSchemePrefix)) != 0 ||
        _stricmp(value.c_str() + value.size() - strlen(kSchemeSuffix), kSchemeSuffix) != 0) return false;
    encoded = value.substr(strlen(kSchemePrefix), value.size() - strlen(kSchemePrefix) - strlen(kSchemeSuffix));
    if (decoded) *decoded = decode_payload(encoded);
    return true;
}

std::string format_track_display_title(const Payload& payload, const std::string& naming_template,
    size_t number, size_t total) {
    if (!payload.is_track()) return payload.get(pf_title);
    if (number < 1) number = 1;
    if (total < number) total = number;
    if (naming_template.empty()) throw std::runtime_error("请输入文件命名格式");

    int width = std::max(2, static_cast<int>(std::to_string(total).size()));
    std::string sequence = std::to_string(number);
    if (static_cast<int>(sequence.size()) < width) sequence.insert(0, width - sequence.size(), '0');
    const auto value = [&](const std::string& token) {
        if (token == "序号") return sequence;
        if (token == "曲名" || token == "文件名") {
            const auto& title = payload.get(pf_title);
            return title.empty() ? std::string("未命名歌曲") : title;
        }
        if (token == "艺术家") {
            const auto& artist = payload.get(pf_artist);
            return artist.empty() ? std::string("未知艺术家") : artist;
        }
        if (token == "专辑名") {
            const auto& album = payload.get(pf_album);
            return album.empty() ? std::string("未知专辑") : album;
        }
        if (token == "音质") {
            const auto& quality = payload.get(pf_preferred_quality);
            return quality.empty() ? std::string("未知音质") : quality;
        }
        throw std::runtime_error("不支持的命名字段：{" + token + "}");
    };

    std::string result;
    for (size_t i = 0; i < naming_template.size(); ++i) {
        if (naming_template[i] == '}') throw std::runtime_error("命名字段的大括号不完整");
        if (naming_template[i] != '{') { result.push_back(naming_template[i]); continue; }
        const auto end = naming_template.find('}', i + 1);
        if (end == std::string::npos) throw std::runtime_error("命名字段的大括号不完整");
        result += value(naming_template.substr(i + 1, end - i - 1));
        i = end;
    }
    for (auto& ch : result)
        if (static_cast<unsigned char>(ch) < 32 || std::string("\\/:*?\"<>|").find(ch) != std::string::npos) ch = '_';
    while (!result.empty() && (result.back() == ' ' || result.back() == '.')) result.pop_back();
    if (result.empty()) result = "在线音乐";
    static const char* reserved[] = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
        "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    if (std::any_of(std::begin(reserved), std::end(reserved),
        [&](const char* name) { return _stricmp(result.c_str(), name) == 0; })) result.insert(result.begin(), '_');
    return result;
}

void payload_to_info(const Payload& payload, file_info& info) {
    payload_to_info(payload, info, 1, 1);
}

void payload_to_info(const Payload& payload, file_info& info, size_t number, size_t total) {
    auto title = payload.get(pf_title);
    if (payload.is_track() && apply_naming_to_search()) {
        try { title = format_track_display_title(payload, download_naming_template(), number, total); }
        catch (...) { title = payload.get(pf_title); }
    }
    info.meta_set("TITLE", title.c_str());
    const bool navigation = payload.is_navigation();
    const bool ranking = is_ranking_payload(payload);
    // Empty tags still evaluate as present in title formatting (for example,
    // an empty TRACKNUMBER becomes 00). Mark name-only rows explicitly so
    // playlist columns can suppress the entire expression, separators included.
    if (navigation || ranking) info.meta_set("LX_TITLE_ONLY", "1");
    else info.meta_remove_field("LX_TITLE_ONLY");
    if (navigation || ranking) {
        for (const auto field : { "ARTIST", "ALBUM", "ALBUM ARTIST", "TRACKNUMBER" })
            info.meta_remove_field(field);
        info.info_remove("length");
        info.set_length(0);
    }
    if (!navigation && !ranking) {
        if (!payload.get(pf_artist).empty()) info.meta_set("ARTIST", payload.get(pf_artist).c_str());
        if (!payload.get(pf_album).empty()) info.meta_set("ALBUM", payload.get(pf_album).c_str());
    }
    info.meta_set("LX_SOURCE", payload.get(pf_platform_label).c_str());
    info.meta_set("LX_KIND", payload.get(pf_kind).c_str());
    info.meta_set("LX_ID", payload.get(pf_id).c_str());
    if (!payload.get(pf_preferred_quality).empty()) info.meta_set("LX_QUALITY", payload.get(pf_preferred_quality).c_str());
    if (navigation) {
        info.set_length(0);
        info.info_set("encoding", "LX search navigation - Enter to open");
    } else if (payload.is_collection()) {
        if (!ranking) info.meta_set("ALBUM", payload.get(pf_platform_label).c_str());
        info.info_set("encoding", "LX collection result - Enter to open");
    } else {
        info.info_set("encoding", "LX online source");
        try {
            const auto milliseconds = std::stoll(payload.get(pf_duration_ms));
            if (milliseconds > 0) info.set_length(milliseconds / 1000.0);
        } catch (...) {}
    }
}

namespace {

void add_payload_info_hint(const metadb_hint_list_v2::ptr& hints, metadb_handle_ptr handle,
    const Payload& payload, t_filetimestamp browse_timestamp, size_t number = 1, size_t total = 1) {
    file_info_impl info;
    payload_to_info(payload, info, number, total);

    // lxmusic:// items have no filesystem timestamp. In foobar2000 v2 a
    // non-fresh hint with invalid stats is intentionally ignored, leaving the
    // encoded URI visible as the track title. A fresh primary hint supplies the
    // virtual input metadata; browse info is the canonical channel for metadata
    // originating from an online playlist and is merged by both fb1 and fb2.
    hints->add_hint(handle, info, filestats_invalid, true);
    hints->add_hint_browse(handle, info, browse_timestamp);
}

} // namespace

void hint_payload_info(metadb_handle_ptr handle, const Payload& payload, size_t number, size_t total) {
    auto hints = metadb_hint_list_v2::create();
    add_payload_info_hint(hints, handle, payload, foobar2000_io::filetimestamp_from_system_timer(), number, total);
    hints->on_done();
}

void refresh_playlist_payload_info() {
    auto playlists = playlist_manager::get();
    auto hints = metadb_hint_list_v2::create();
    const auto browse_timestamp = foobar2000_io::filetimestamp_from_system_timer();
    bool have_hints = false;

    for (t_size playlist = 0; playlist < playlists->get_playlist_count(); ++playlist) {
        install_collection_actions(playlist);
        metadb_handle_list items;
        playlists->playlist_get_all_items(playlist, items);
        size_t track_total = 0;
        for (t_size index = 0; index < items.get_count(); ++index) {
            try {
                std::string encoded;
                Payload payload;
                if (uri_to_payload(items[index]->get_path(), encoded, &payload) && payload.is_track()) ++track_total;
            } catch (...) {}
        }
        size_t track_number = 0;
        for (t_size index = 0; index < items.get_count(); ++index) {
            try {
                std::string encoded;
                Payload payload;
                if (!uri_to_payload(items[index]->get_path(), encoded, &payload)) continue;
                const auto number = payload.is_track() ? ++track_number : size_t(1);
                add_payload_info_hint(hints, items[index], payload, browse_timestamp, number,
                    payload.is_track() ? track_total : size_t(1));
                have_hints = true;
            } catch (...) {
                // A malformed legacy entry must not prevent foobar2000 startup.
            }
        }
    }

    if (have_hints) hints->on_done();
}

std::string component_directory() {
    std::string path = core_api::get_my_full_path();
    const auto slash = path.find_last_of("\\/");
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

std::string data_directory() {
    const auto native = foobar2000_io::filesystem::g_get_native_path(core_api::get_profile_path());
    std::string path = native.c_str();
    if (!path.empty() && path.back() != '\\' && path.back() != '/') path.push_back('\\');
    path += "foo_lxmusic";
    return path;
}

void show_status(const std::string& message) {
    static service_ptr_t<ui_status_text_override> status;
    if (!status.is_valid()) ui_control::get()->override_status_text_create(status);
    if (status.is_valid()) status->override_text(message.c_str());
}

void copy_text_to_clipboard(const std::string& text) {
    if (text.empty() || text.size() > 65536 || text.find('\0') != std::string::npos)
        throw std::runtime_error("无法复制无效文本");
    const std::wstring value = pfc::stringcvt::string_wide_from_utf8(text.c_str()).get_ptr();
    const auto bytes = (value.size() + 1) * sizeof(wchar_t);
    const auto memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (!memory) throw std::runtime_error("无法分配剪贴板内存");
    const auto data = GlobalLock(memory);
    if (!data) { GlobalFree(memory); throw std::runtime_error("无法写入剪贴板"); }
    std::memcpy(data, value.c_str(), bytes);
    GlobalUnlock(memory);
    if (!OpenClipboard(core_api::get_main_window())) {
        GlobalFree(memory);
        throw std::runtime_error("剪贴板正被占用，请稍后重试");
    }
    const bool copied = EmptyClipboard() && SetClipboardData(CF_UNICODETEXT, memory) != nullptr;
    CloseClipboard();
    if (!copied) {
        GlobalFree(memory);
        throw std::runtime_error("无法复制到剪贴板，请重试");
    }
}

BridgeResult run_bridge(const std::vector<std::string>& args, abort_callback& abort,
    BridgeLineCallback on_line, const std::string* standard_input) {
    if (args.empty()) throw std::runtime_error("缺少桥接命令");
    const auto directory = component_directory();
    const auto executable = directory + "\\foo_lx_bridge.exe";
    const auto executable_w = wide(executable);
    if (GetFileAttributesW(executable_w.c_str()) == INVALID_FILE_ATTRIBUTES)
        throw std::runtime_error("组件包缺少 foo_lx_bridge.exe，请重新安装完整组件包");

    std::vector<std::string> actual;
    actual.push_back(args.front());
    actual.push_back(base64url_encode_text(data_directory()));
    actual.insert(actual.end(), args.begin() + 1, args.end());

    std::wstring command = L"\"" + executable_w + L"\"";
    for (const auto& arg : actual) {
        command += L" ";
        command += wide(arg);
    }

    SECURITY_ATTRIBUTES security{ sizeof(security), nullptr, TRUE };
    HANDLE read_raw = nullptr, write_raw = nullptr;
    if (!CreatePipe(&read_raw, &write_raw, &security, 0)) throw std::runtime_error("无法创建桥接进程管道");
    win_handle read_pipe(read_raw), write_pipe(write_raw);
    SetHandleInformation(read_pipe.get(), HANDLE_FLAG_INHERIT, 0);
    HANDLE input_read_raw = nullptr, input_write_raw = nullptr;
    if (standard_input && !CreatePipe(&input_read_raw, &input_write_raw, &security, 0))
        throw std::runtime_error("无法创建桥接输入管道");
    win_handle input_read(input_read_raw), input_write(input_write_raw);
    if (standard_input) SetHandleInformation(input_write.get(), HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    startup.hStdOutput = write_pipe.get();
    startup.hStdError = write_pipe.get();
    startup.hStdInput = standard_input ? input_read.get() : GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION process{};
    std::vector<wchar_t> mutable_command(command.begin(), command.end());
    mutable_command.push_back(0);
    const auto directory_w = wide(directory);
    if (!CreateProcessW(executable_w.c_str(), mutable_command.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW, nullptr, directory_w.c_str(), &startup, &process)) {
        throw std::runtime_error("无法启动 LX 音源桥接进程");
    }
    win_handle process_handle(process.hProcess), thread_handle(process.hThread);
    write_pipe.reset();
    input_read.reset();
    if (standard_input) {
        size_t offset = 0;
        bool write_failed = false;
        while (offset < standard_input->size()) {
            DWORD written = 0;
            const auto count = static_cast<DWORD>(std::min<size_t>(standard_input->size() - offset, 65536));
            if (!WriteFile(input_write.get(), standard_input->data() + offset, count, &written, nullptr) || written == 0) {
                write_failed = true;
                break;
            }
            offset += written;
        }
        input_write.reset();
        if (write_failed) {
            WaitForSingleObject(process_handle.get(), 5000);
            throw std::runtime_error("无法把音源脚本写入编辑进程");
        }
    }

    BridgeResult result;
    char buffer[8192];
    size_t parsed_offset = 0;
    const auto consume_lines = [&](bool flush_last) {
        for (;;) {
            const auto end = result.raw.find('\n', parsed_offset);
            if (end == std::string::npos && !flush_last) break;
            if (end == std::string::npos && parsed_offset >= result.raw.size()) break;
            const auto count = end == std::string::npos ? std::string::npos : end - parsed_offset;
            auto text = trim_line(result.raw.substr(parsed_offset, count));
            parsed_offset = end == std::string::npos ? result.raw.size() : end + 1;
            if (!text.empty()) {
                result.lines.push_back({ split(text, '\t') });
                if (on_line) on_line(result.lines.back());
            }
            if (end == std::string::npos) break;
        }
    };
    bool finished = false;
    while (!finished) {
        if (abort.is_aborting()) {
            TerminateProcess(process_handle.get(), ERROR_CANCELLED);
            WaitForSingleObject(process_handle.get(), 5000);
            throw exception_aborted();
        }
        DWORD available = 0;
        if (PeekNamedPipe(read_pipe.get(), nullptr, 0, nullptr, &available, nullptr) && available > 0) {
            DWORD read = 0;
            const DWORD wanted = std::min<DWORD>(available, static_cast<DWORD>(sizeof(buffer)));
            if (ReadFile(read_pipe.get(), buffer, wanted, &read, nullptr) && read > 0) {
                result.raw.append(buffer, read);
                consume_lines(false);
            }
        } else {
            finished = WaitForSingleObject(process_handle.get(), 25) == WAIT_OBJECT_0;
        }
    }
    for (;;) {
        DWORD available = 0;
        if (!PeekNamedPipe(read_pipe.get(), nullptr, 0, nullptr, &available, nullptr) || available == 0) break;
        DWORD read = 0;
        if (!ReadFile(read_pipe.get(), buffer, std::min<DWORD>(available, static_cast<DWORD>(sizeof(buffer))), &read, nullptr) || read == 0) break;
        result.raw.append(buffer, read);
        consume_lines(false);
    }
    GetExitCodeProcess(process_handle.get(), &result.exit_code);
    consume_lines(true);
    if (result.exit_code != 0) throw std::runtime_error(bridge_error(result));
    return result;
}

BridgeResult run_bridge_noabort(const std::vector<std::string>& args) {
    return run_bridge(args, fb2k::noAbort);
}

std::vector<std::string> bridge_items(const BridgeResult& result) {
    std::vector<std::string> items;
    for (const auto& line : result.lines)
        if (line.fields.size() >= 2 && line.fields[0] == "ITEM") items.push_back(line.fields[1]);
    return items;
}

std::string bridge_message(const BridgeResult& result, const char* record) {
    for (const auto& line : result.lines) {
        if (line.fields.size() >= 2 && line.fields[0] == record) return base64url_decode(line.fields.back());
    }
    return {};
}

std::string bridge_error(const BridgeResult& result) {
    for (const auto& line : result.lines) {
        if (line.fields.size() >= 2 && line.fields[0] == "ERROR") {
            try { return base64url_decode(line.fields[1]); } catch (...) { return line.fields[1]; }
        }
    }
    auto raw = trim_line(result.raw);
    if (raw.size() > 1000) raw.resize(1000);
    return raw.empty() ? "LX 音源桥接进程执行失败" : raw;
}

t_size add_payloads_to_playlist(const std::vector<std::string>& payloads, const std::string& playlist_name,
    t_size preserve_playlist, bool create_new) {
    if (payloads.empty()) throw std::runtime_error("没有可加入播放列表的结果");
    auto playlists = playlist_manager::get();
    auto index = create_new ? playlists->create_playlist(playlist_name.c_str(), pfc_infinite, pfc_infinite)
        : playlists->find_or_create_playlist(playlist_name.c_str());
    if (index == preserve_playlist)
        index = playlists->create_playlist(playlist_name.c_str(), pfc_infinite, pfc_infinite);
    reset_playlist_navigation(index);
    replace_playlist_payloads(payloads, index);
    return index;
}

void replace_playlist_payloads(const std::vector<std::string>& payloads, t_size index, bool activate) {
    if (payloads.empty()) throw std::runtime_error("没有可加入播放列表的结果");
    metadb_handle_list items;
    auto hints = metadb_hint_list_v2::create();
    const auto browse_timestamp = foobar2000_io::filetimestamp_from_system_timer();
    size_t track_total = 0;
    for (const auto& encoded : payloads) {
        if (decode_payload(encoded).is_track()) ++track_total;
    }
    size_t track_number = 0;
    for (const auto& encoded : payloads) {
        const auto payload = decode_payload(encoded);
        const auto uri = payload_to_uri(encoded);
        auto handle = metadb::get()->handle_create(uri.c_str(), 0);
        const auto number = payload.is_track() ? ++track_number : size_t(1);
        add_payload_info_hint(hints, handle, payload, browse_timestamp, number,
            payload.is_track() ? track_total : size_t(1));
        items.add_item(handle);
    }
    // Commit before exposing the handles to the playlist, so the first title
    // formatting pass already sees artist/title instead of the encoded URI.
    hints->on_done();
    auto playlists = playlist_manager::get();
    playlists->playlist_undo_backup(index);
    playlists->playlist_clear(index);
    playlists->playlist_add_items(index, items, pfc::bit_array_one(0));
    install_collection_actions(index);
    if (activate) playlists->set_active_playlist(index);
    playlists->playlist_set_focus_item(index, 0);
    if (activate) playlists->playlist_ensure_visible(index, 0);
}

metadb_handle_list selected_playlist_items() {
    metadb_handle_list items;
    auto playlists = playlist_manager::get();
    playlists->activeplaylist_get_selected_items(items);
    if (items.get_count() == 0) {
        metadb_handle_ptr focused;
        if (playlists->activeplaylist_get_focus_item_handle(focused)) items.add_item(focused);
    }
    return items;
}

std::string payload_for_handle(metadb_handle_ptr handle) {
    if (handle.is_empty()) return {};
    std::string encoded;
    return uri_to_payload(handle->get_path(), encoded, nullptr) ? encoded : std::string();
}

std::string lyric_payload_for_handle(metadb_handle_ptr handle) {
    const auto existing = payload_for_handle(handle);
    if (!existing.empty()) return existing;
    if (handle.is_empty()) throw std::runtime_error("没有选中歌曲");

    std::vector<std::string> fields(pf_count);
    fields[pf_version] = "1";
    fields[pf_kind] = "track";
    fields[pf_platform_label] = "本地/其他";
    fields[pf_id] = handle->get_path();
    fields[pf_title] = title_field(handle, "%title%");
    if (fields[pf_title].empty()) fields[pf_title] = title_field(handle, "%filename%");
    fields[pf_artist] = title_field(handle, "%artist%");
    fields[pf_album] = title_field(handle, "%album%");
    fields[pf_duration_ms] = std::to_string(static_cast<long long>(std::max(0.0, handle->get_length()) * 1000.0));
    fields[pf_preferred_quality] = "flac";
    fields[pf_music_info_json] = "{}";
    if (fields[pf_title].empty()) throw std::runtime_error("所选歌曲没有可用于搜索歌词的标题");
    return encode_payload(fields);
}

} // namespace lxmusic
