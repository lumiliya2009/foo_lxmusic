#pragma once
// A small host boundary double for exercising the real input_lxmusic.cpp.
// GUI integration is separately checked with prepare-host.ps1.
#define NOMINMAX
#include <windows.h>
#include <atomic>
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <functional>
#include <future>
#include <iostream>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>
#include <map>

struct exception_aborted : std::runtime_error { exception_aborted() : runtime_error("aborted") {} };
struct exception_io_data : std::runtime_error { using runtime_error::runtime_error; };
struct exception_io_unsupported_format : exception_io_data { exception_io_unsupported_format() : exception_io_data("unsupported") {} };
struct exception_tagging_unsupported : std::runtime_error { exception_tagging_unsupported() : runtime_error("tagging") {} };
struct abort_state { std::mutex mutex; std::condition_variable changed; bool aborted = false; };
struct abort_callback {
    std::shared_ptr<abort_state> state = std::make_shared<abort_state>();
    void abort() { std::lock_guard<std::mutex> lock(state->mutex); state->aborted = true; state->changed.notify_all(); }
    bool is_aborting() const { std::lock_guard<std::mutex> lock(state->mutex); return state->aborted; }
    void check() const { if (is_aborting()) throw exception_aborted(); }
    void sleep(double) const {
        std::unique_lock<std::mutex> lock(state->mutex);
        if (!state->changed.wait_for(lock, std::chrono::seconds(5), [&] { return state->aborted; }))
            throw std::runtime_error("test timeout: decoder was never cancelled");
        throw exception_aborted();
    }
};
template<class T> struct service_ptr_t : std::shared_ptr<T> {
    using std::shared_ptr<T>::shared_ptr;
    bool is_valid() const { return this->get() != nullptr; }
    void release() { this->reset(); }
};
namespace pfc { using string_base = std::string; using string8 = std::string; }
struct file {
    using ptr = service_ptr_t<file>;
    virtual ~file() = default;
    virtual bool get_content_type(pfc::string_base&) = 0;
    virtual size_t read(void*, size_t, abort_callback&) = 0;
    virtual void reopen(abort_callback&) = 0;
    virtual bool can_seek() = 0;
};
struct file_chain : file {
    explicit file_chain(file::ptr source) : source(source) {}
    bool get_content_type(pfc::string_base& out) override { return source->get_content_type(out); }
    size_t read(void* out, size_t size, abort_callback& abort) override { return source->read(out, size, abort); }
    void reopen(abort_callback& abort) override { source->reopen(abort); }
    bool can_seek() override { return source->can_seek(); }
    file::ptr source;
};
struct file_cached { static file::ptr g_create(file::ptr source, abort_callback&, size_t) { return source; } };
using t_size = size_t;
struct file_info {
    std::map<std::string, std::string> info, meta;
    void copy_info(const file_info& other) { info = other.info; }
    void info_set(const char* key, const char* value) { info[key] = value; }
    const char* info_get(const char* key) const { auto item = info.find(key); return item == info.end() ? nullptr : item->second.c_str(); }
    size_t info_get_count() const { return info.size(); }
    const char* info_enum_name(size_t index) const { auto item = info.begin(); std::advance(item, index); return item->first.c_str(); }
    const char* info_enum_value(size_t index) const { auto item = info.begin(); std::advance(item, index); return item->second.c_str(); }
    void reset() { info.clear(); meta.clear(); }
};
using file_info_impl = file_info;
struct audio_chunk {
    double duration = 2;
    unsigned sample_rate = 48000, channels = 2;
    double get_duration() const { return duration; }
    unsigned get_sample_rate() const { return sample_rate; }
    unsigned get_channels() const { return channels; }
};
struct input_stubs {};
enum t_input_open_reason { input_open_decode, input_open_info_write };
constexpr unsigned input_flag_playback = 1 << 2;
using t_filestats2 = int;
constexpr int filestats2_invalid = 0;
template<class T> struct input_singletrack_factory_t {};

namespace test_host {
enum class failure { none, resolve, empty_url, open, initialize, run, seek, cancelled };
inline failure fail = failure::none;
inline bool collection = false;
inline bool navigation = false;
inline int resolve_calls = 0, stops = 0;
inline std::string popup_title, popup_text;
inline std::string stream_bytes, stream_mime, decoder_mime, decoder_prefix;
inline bool stream_seekable = true;
inline size_t stream_read_limit = SIZE_MAX;
inline std::vector<std::string> quality_sequence, requested_payloads, exclusions, requested_qualities;
inline std::vector<std::string> pinned_sources;
inline std::string current_quality, media_error, error_stage;
inline bool duplicate_url = false, repeat_quality = false, dynamic_bitrate = false, no_seek = false, invalid_media = false;
inline int source_attempts = 0, media_opens = 0, abort_on_open = 0, run_chunks = 0;
inline int source_fail_at = 0;
inline std::vector<std::string> failed_qualities;
inline std::vector<double> seek_positions;
inline abort_callback* playback_abort = nullptr;
inline std::mutex queue_mutex;
inline std::condition_variable queued;
inline std::vector<std::function<void()>> callbacks;
inline void check_failure(failure at) {
    if (fail == at) throw std::runtime_error("fixture failure " + std::to_string(static_cast<int>(at)));
}
inline void reset(failure mode) {
    fail = mode; collection = navigation = false; resolve_calls = stops = 0;
    popup_title.clear(); popup_text.clear(); callbacks.clear(); playback_abort = nullptr;
    stream_bytes = "fLaC_fixture_audio_payload"; stream_mime = "audio/x-ogg";
    decoder_mime.clear(); decoder_prefix.clear(); stream_seekable = true; stream_read_limit = SIZE_MAX;
    quality_sequence.clear(); requested_payloads.clear(); exclusions.clear(); requested_qualities.clear();
    pinned_sources.clear();
    current_quality.clear(); media_error.clear(); error_stage.clear();
    duplicate_url = repeat_quality = dynamic_bitrate = no_seek = invalid_media = false;
    source_attempts = media_opens = abort_on_open = run_chunks = source_fail_at = 0;
    failed_qualities.clear(); seek_positions.clear();
}
inline void media_failure(const char* stage) {
    if (error_stage == stage && std::find(failed_qualities.begin(), failed_qualities.end(), current_quality) != failed_qualities.end()) {
        if (invalid_media) throw exception_io_unsupported_format();
        throw std::runtime_error(media_error);
    }
}
}
namespace fb2k {
template<class T, class... Args> service_ptr_t<T> service_new(Args&&... args) {
    return service_ptr_t<T>(new T(std::forward<Args>(args)...));
}
inline void inMainThread(std::function<void()> call, abort_callback& abort) {
    // Matches the SDK's abort_callback_clone + deferred abort check contract.
    auto token = abort;
    std::lock_guard<std::mutex> lock(test_host::queue_mutex);
    test_host::callbacks.push_back([token, call] { if (!token.is_aborting()) call(); });
    test_host::queued.notify_all();
}
}
struct fixture_stream : file {
    size_t position = 0;
    bool get_content_type(pfc::string_base& out) override { out = test_host::stream_mime; return !out.empty(); }
    size_t read(void* out, size_t size, abort_callback& abort) override {
        abort.check();
        size = std::min({size, test_host::stream_bytes.size() - position, test_host::stream_read_limit});
        memcpy(out, test_host::stream_bytes.data() + position, size);
        position += size;
        return size;
    }
    void reopen(abort_callback& abort) override { abort.check(); position = 0; }
    bool can_seek() override { return test_host::stream_seekable; }
};
struct filesystem {
    static void g_open_read(file::ptr& out, const char*, abort_callback& abort) {
        ++test_host::media_opens;
        if (test_host::abort_on_open == test_host::media_opens) abort.abort();
        test_host::media_failure("open");
        abort.check(); out.reset(new fixture_stream);
    }
};
struct playback_control {
    static playback_control* get() { static playback_control instance; return &instance; }
    void stop() { ++test_host::stops; test_host::playback_abort->abort(); }
};
struct popup_message {
    static void g_complain(const char* title, const char* message) {
        if (!test_host::playback_abort->is_aborting()) throw std::runtime_error("popup appeared before playback stopped");
        test_host::popup_title = title; test_host::popup_text = message;
    }
};
struct input_decoder {
    using ptr = service_ptr_t<input_decoder>;
    void initialize(unsigned, unsigned, abort_callback&) { test_host::media_failure("initialize"); test_host::check_failure(test_host::failure::initialize); }
    bool run(audio_chunk&, abort_callback&) {
        test_host::check_failure(test_host::failure::run);
        if (test_host::run_chunks > 0) { --test_host::run_chunks; return true; }
        test_host::media_failure("run"); return false;
    }
    void seek(double position, abort_callback&) {
        test_host::seek_positions.push_back(position);
        test_host::media_failure("seek"); test_host::check_failure(test_host::failure::seek);
    }
    bool can_seek() { return !test_host::no_seek; }
    void get_info(unsigned, file_info& info, abort_callback&) {
        info.info_set("codec", test_host::current_quality == "320k" ? "MP3" : "FLAC");
        info.info_set("samplerate", "44100"); info.info_set("bitrate", "320"); info.info_set("channels", "2");
        info.meta["TITLE"] = "untrusted media title";
    }
    bool get_dynamic_info(file_info& info, double& delta) {
        if (!test_host::dynamic_bitrate) return false;
        info.info_set("bitrate", "287"); delta = -0.01; return true;
    }
    bool get_dynamic_info_track(file_info&, double&) { return false; }
    void on_idle(abort_callback&) {}
};
struct input_entry {
    static void g_open_for_decoding(input_decoder::ptr& decoder, file::ptr hint, const char*, abort_callback& abort, bool) {
        test_host::check_failure(test_host::failure::open);
        if (!hint.is_valid()) throw std::runtime_error("decoder needs the inspected stream");
        hint->get_content_type(test_host::decoder_mime);
        char prefix[16]{};
        const auto count = hint->read(prefix, sizeof(prefix), abort);
        test_host::decoder_prefix.assign(prefix, count);
        decoder.reset(new input_decoder);
    }
};
namespace lxmusic {
constexpr const char* kSchemePrefix = "lxmusic://item/";
enum { pf_preferred_quality, pf_title, pf_artist };
struct Payload {
    bool is_track() const { return !test_host::collection && !test_host::navigation; }
    bool is_navigation() const { return test_host::navigation; }
    const std::string& get(int index) const {
        static const std::string values[] = { "flac", "测试歌曲", "测试歌手" };
        return values[index];
    }
};
struct BridgeLine { std::vector<std::string> fields; };
struct BridgeResult { std::vector<BridgeLine> lines; };
inline bool uri_to_payload(const char*, std::string& encoded, Payload*) { encoded = "fixture"; return true; }
inline void payload_to_info(const Payload&, file_info& info) { info.meta["TITLE"] = "测试歌曲"; }
inline std::string base64url_decode(const std::string& text) { return text; }
inline BridgeResult run_bridge(const std::vector<std::string>& args, abort_callback& abort,
    std::function<void(const BridgeLine&)> on_line = {}) {
    abort.check();
    ++test_host::resolve_calls;
    if (test_host::fail == test_host::failure::cancelled) throw exception_aborted();
    test_host::check_failure(test_host::failure::resolve);
    if (test_host::fail == test_host::failure::empty_url) return {};
    if (!test_host::quality_sequence.empty()) {
        test_host::pinned_sources.push_back(args.size() > 4 ? args[4] : "");
        if (on_line) on_line({{"SOURCEID", "1234567890abcdef1234567890abcdef"}});
        test_host::requested_payloads.push_back(args.at(1));
        test_host::exclusions.push_back(args.size() > 3 ? args[3] : "");
        const auto index = test_host::repeat_quality ? 0 : test_host::source_attempts;
        if (index >= static_cast<int>(test_host::quality_sequence.size())) throw std::runtime_error("quality list exhausted");
        auto quality = test_host::quality_sequence[index];
        test_host::current_quality = quality;
        test_host::requested_qualities.push_back(quality);
        ++test_host::source_attempts;
        if (on_line) on_line({{"TRIED", quality}});
        if (test_host::source_attempts == test_host::source_fail_at) throw std::runtime_error("音源鉴权失败");
        auto url = test_host::duplicate_url ? std::string("fixture.wav") : quality + ".wav";
        bool more = test_host::repeat_quality || test_host::source_attempts < static_cast<int>(test_host::quality_sequence.size());
        return {{{{"URL", url, quality}}, {{"REMAINING", more ? "1" : "0"}}}};
    }
    return { { { { "URL", "fixture.wav" } } } };
}
}
