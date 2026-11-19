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

struct exception_aborted : std::runtime_error { exception_aborted() : runtime_error("aborted") {} };
struct exception_io_data : std::runtime_error { using runtime_error::runtime_error; };
struct exception_io_unsupported_format : std::runtime_error { exception_io_unsupported_format() : runtime_error("unsupported") {} };
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
struct file_info {};
struct audio_chunk {};
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
inline int resolve_calls = 0, stops = 0;
inline std::string popup_title, popup_text;
inline std::string stream_bytes, stream_mime, decoder_mime, decoder_prefix;
inline bool stream_seekable = true;
inline size_t stream_read_limit = SIZE_MAX;
inline abort_callback* playback_abort = nullptr;
inline std::mutex queue_mutex;
inline std::condition_variable queued;
inline std::vector<std::function<void()>> callbacks;
inline void check_failure(failure at) {
    if (fail == at) throw std::runtime_error("fixture failure " + std::to_string(static_cast<int>(at)));
}
inline void reset(failure mode) {
    fail = mode; collection = false; resolve_calls = stops = 0;
    popup_title.clear(); popup_text.clear(); callbacks.clear(); playback_abort = nullptr;
    stream_bytes = "fLaC_fixture_audio_payload"; stream_mime = "audio/x-ogg";
    decoder_mime.clear(); decoder_prefix.clear(); stream_seekable = true; stream_read_limit = SIZE_MAX;
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
    void initialize(unsigned, unsigned, abort_callback&) { test_host::check_failure(test_host::failure::initialize); }
    bool run(audio_chunk&, abort_callback&) { test_host::check_failure(test_host::failure::run); return false; }
    void seek(double, abort_callback&) { test_host::check_failure(test_host::failure::seek); }
    bool can_seek() { return true; }
    bool get_dynamic_info(file_info&, double&) { return false; }
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
    bool is_track() const { return !test_host::collection; }
    const std::string& get(int index) const {
        static const std::string values[] = { "flac", "测试歌曲", "测试歌手" };
        return values[index];
    }
};
struct BridgeLine { std::vector<std::string> fields; };
struct BridgeResult { std::vector<BridgeLine> lines; };
inline bool uri_to_payload(const char*, std::string& encoded, Payload*) { encoded = "fixture"; return true; }
inline void payload_to_info(const Payload&, file_info&) {}
inline std::string base64url_decode(const std::string& text) { return text; }
inline BridgeResult run_bridge(const std::vector<std::string>&, abort_callback&) {
    ++test_host::resolve_calls;
    if (test_host::fail == test_host::failure::cancelled) throw exception_aborted();
    test_host::check_failure(test_host::failure::resolve);
    if (test_host::fail == test_host::failure::empty_url) return {};
    return { { { { "URL", "fixture.wav" } } } };
}
}
