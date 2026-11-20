// Appended after the actual production input source by run-input-regression.ps1.
void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

std::string decode_operation(input_lxmusic& input, abort_callback& abort, unsigned flags) {
    try {
        input.open({}, "fixture", input_open_decode, abort);
        input.decode_initialize(flags, abort);
        if (test_host::fail == test_host::failure::seek) input.decode_seek(2, abort);
        audio_chunk chunk;
        require(!input.decode_run(chunk, abort), "fixture must end at EOF");
        return "eof";
    } catch (const exception_aborted&) { return "aborted"; }
    catch (const std::exception&) { return "error"; }
}

void test_playback_failure(test_host::failure mode, bool cancel_before_dispatch = false, bool collection = false) {
    test_host::reset(mode);
    test_host::collection = collection;
    abort_callback abort;
    test_host::playback_abort = &abort;
    input_lxmusic input;
    auto worker = std::async(std::launch::async, [&] { return decode_operation(input, abort, input_flag_playback); });
    std::function<void()> callback;
    {
        std::unique_lock<std::mutex> lock(test_host::queue_mutex);
        bool ready = test_host::queued.wait_for(lock, std::chrono::seconds(2), [] { return !test_host::callbacks.empty(); });
        if (!ready) { abort.abort(); require(false, "playback error did not schedule a notification"); }
        require(test_host::callbacks.size() == 1, "duplicate error notifications");
        callback = test_host::callbacks.front();
    }
    // The host must not receive a decode error and move to the next song while
    // the stop callback is still queued.
    require(worker.wait_for(std::chrono::milliseconds(40)) == std::future_status::timeout,
        "decoder escaped before stop: host could auto-skip");
    if (cancel_before_dispatch) abort.abort();
    callback();
    require(worker.wait_for(std::chrono::seconds(2)) == std::future_status::ready, "stop did not release decoder");
    require(worker.get() == "aborted", "playback error reached host as a skippable error");
    if (cancel_before_dispatch) {
        require(test_host::stops == 0 && test_host::popup_text.empty(), "stale callback affected new playback");
    } else {
        require(test_host::stops == 1, "playback was not stopped exactly once");
        require(test_host::popup_title == "LX 播放失败", "missing failure dialog");
        require(test_host::popup_text.find("测试歌曲") != std::string::npos, "missing failed song title");
        require(test_host::popup_text.find("测试歌手") != std::string::npos, "missing failed artist");
        const char* reason = collection ? "按回车打开" :
            mode == test_host::failure::empty_url ? "没有返回可播放地址" : "fixture failure";
        require(test_host::popup_text.find(reason) != std::string::npos, "missing failure reason");
    }
    require(test_host::resolve_calls == (collection ? 0 : 1), "unexpected extra source request");
}

void test_stream_type(const std::string& bytes, const std::string& reported, const std::string& expected,
    bool seekable, size_t read_limit = SIZE_MAX) {
    test_host::reset(test_host::failure::none);
    test_host::stream_bytes = bytes;
    test_host::stream_mime = reported;
    test_host::stream_seekable = seekable;
    test_host::stream_read_limit = read_limit;
    abort_callback abort;
    auto stream = lxmusic::open_audio_stream("http://fixture.invalid/song.flac?token=unchanged", abort);
    pfc::string8 actual_type;
    stream->get_content_type(actual_type);
    require(actual_type == expected, "incorrect decoder MIME type");
    require(stream->can_seek() == seekable, "seek support was changed");
    for (int pass = 0; pass < 2; ++pass) {
        std::string readback;
        char buffer[7];
        for (;;) {
            const auto count = stream->read(buffer, sizeof(buffer), abort);
            if (!count) break;
            readback.append(buffer, count);
        }
        require(readback == bytes, "sniffing or reopen dropped/duplicated audio bytes");
        stream->reopen(abort);
    }
}

void quality_fixture(const std::string& stage = "open", const std::string& error = "Forbidden (403)") {
    test_host::reset(test_host::failure::none);
    test_host::quality_sequence = { "flac", "320k", "128k" };
    test_host::failed_qualities = { "flac" };
    test_host::error_stage = stage;
    test_host::media_error = error;
}

void expect_stopped(const std::function<void(input_lxmusic&, abort_callback&)>& action, const char* reason) {
    abort_callback abort;
    test_host::playback_abort = &abort;
    input_lxmusic input;
    auto worker = std::async(std::launch::async, [&] {
        try { action(input, abort); return std::string("completed"); }
        catch (const exception_aborted&) { return std::string("aborted"); }
        catch (const std::exception&) { return std::string("error"); }
    });
    std::function<void()> callback;
    {
        std::unique_lock<std::mutex> lock(test_host::queue_mutex);
        bool ready = test_host::queued.wait_for(lock, std::chrono::seconds(2), [] { return !test_host::callbacks.empty(); });
        if (!ready) { abort.abort(); require(false, "quality failure did not schedule stop"); }
        require(test_host::callbacks.size() == 1, "quality retries produced multiple dialogs");
        callback = test_host::callbacks.front();
    }
    require(worker.wait_for(std::chrono::milliseconds(40)) == std::future_status::timeout,
        "quality failure escaped before stop and could skip song");
    callback();
    require(worker.wait_for(std::chrono::seconds(2)) == std::future_status::ready, "quality stop did not release decoder");
    require(worker.get() == "aborted", "quality failure reached host as a skippable error");
    require(test_host::stops == 1, "quality failure did not stop exactly once");
    require(test_host::popup_text.find(reason) != std::string::npos, "quality failure reason missing");
    require(test_host::popup_text.find("已尝试音质") != std::string::npos, "attempted qualities missing");
}

void open_playback(input_lxmusic& input, abort_callback& abort) {
    input.open({}, "fixture", input_open_decode, abort);
    input.decode_initialize(input_flag_playback, abort);
}

void require_info(const file_info& info, const char* key, const char* value) {
    const auto actual = info.info_get(key);
    require(actual && std::string(actual) == value, key);
}

int test_quality_and_info() {
    int count = 0;
    for (const char* stage : { "open", "initialize", "run", "seek" }) {
        quality_fixture(stage);
        abort_callback abort;
        input_lxmusic input;
        open_playback(input, abort);
        if (std::string(stage) == "seek") input.decode_seek(12.5, abort);
        audio_chunk chunk;
        require(!input.decode_run(chunk, abort), "fallback fixture did not end");
        require(test_host::requested_qualities == std::vector<std::string>({ "flac", "320k" }), "fallback skipped or repeated a quality");
        require(test_host::exclusions == std::vector<std::string>({ "", "flac" }), "failed quality was not excluded");
        require(test_host::pinned_sources == std::vector<std::string>({ "", "1234567890abcdef1234567890abcdef" }), "fallback did not stay with the original source");
        require(test_host::requested_payloads == std::vector<std::string>({ "fixture", "fixture" }), "fallback changed song");
        require(test_host::callbacks.empty() && test_host::stops == 0, "successful fallback interrupted playback");
        if (std::string(stage) == "seek")
            require(test_host::seek_positions == std::vector<double>({ 12.5, 12.5 }), "seek fallback lost requested position");
        file_info info;
        double delta = 99;
        require(input.decode_get_dynamic_info(info, delta), "fallback did not publish technical information");
        require_info(info, "codec", "MP3");
        require_info(info, "lx_playback_quality", "320k");
        ++count;
    }
    for (const char* error : { "Not Found (404)", "Gone (410)", "HTTP 500", "Bad Gateway (502)", "Unavailable (503)" }) {
        quality_fixture("open", error);
        abort_callback abort;
        input_lxmusic input;
        open_playback(input, abort);
        require(test_host::media_opens == 2, "eligible HTTP failure did not try another quality");
        ++count;
    }
    quality_fixture("initialize");
    test_host::invalid_media = true;
    {
        abort_callback abort;
        input_lxmusic input;
        open_playback(input, abort);
        require(test_host::media_opens == 2, "invalid audio did not try another quality");
    }
    ++count;

    quality_fixture();
    test_host::failed_qualities = test_host::quality_sequence;
    expect_stopped(open_playback, "403");
    require(test_host::resolve_calls == 3 && test_host::media_opens == 3, "all-quality failure was not bounded");
    require(test_host::exclusions.back() == "flac,320k", "exclusions were not accumulated");
    ++count;

    for (const char* error : { "Unauthorized (401)", "Too Many Requests (429)", "failure 1403", "disk failure" }) {
        quality_fixture("open", error);
        expect_stopped(open_playback, error);
        require(test_host::resolve_calls == 1, "fatal/unrelated error retried qualities");
        ++count;
    }
    quality_fixture();
    test_host::source_fail_at = 2;
    expect_stopped(open_playback, "鉴权失败");
    require(test_host::resolve_calls == 2 && test_host::media_opens == 1, "source authentication failure was retried");
    require(test_host::popup_text.find("403") != std::string::npos, "last media error was lost after source failure");
    ++count;

    quality_fixture();
    test_host::abort_on_open = 2;
    {
        abort_callback abort;
        input_lxmusic input;
        require(decode_operation(input, abort, input_flag_playback) == "aborted", "fallback cancellation did not abort");
        require(test_host::resolve_calls == 2 && test_host::callbacks.empty(), "cancellation retried or showed an error");
    }
    ++count;

    quality_fixture();
    test_host::duplicate_url = true;
    expect_stopped(open_playback, "重复返回");
    require(test_host::media_opens == 1 && test_host::resolve_calls == 3, "duplicate media URL was fetched repeatedly");
    ++count;

    quality_fixture();
    test_host::repeat_quality = true;
    expect_stopped(open_playback, "重复返回");
    require(test_host::resolve_calls == 2, "malformed repeated quality caused a loop");
    ++count;

    quality_fixture("run");
    test_host::run_chunks = 1;
    {
        abort_callback abort;
        input_lxmusic input;
        open_playback(input, abort);
        audio_chunk chunk;
        require(input.decode_run(chunk, abort), "initial chunk missing");
        file_info info;
        input.get_info(info, abort);
        require_info(info, "samplerate", "48000");
        require(!input.decode_run(chunk, abort), "resumed fixture did not end");
        require(test_host::seek_positions == std::vector<double>({ 2 }), "runtime fallback restarted song");
        double delta = 0;
        require(input.decode_get_dynamic_info(info, delta), "runtime switch was not published");
        require_info(info, "codec", "MP3");
        require_info(info, "samplerate", "44100");
    }
    ++count;

    quality_fixture("run");
    test_host::run_chunks = 1;
    test_host::no_seek = true;
    expect_stopped([](input_lxmusic& input, abort_callback& abort) {
        open_playback(input, abort);
        audio_chunk chunk;
        input.decode_run(chunk, abort);
        input.decode_run(chunk, abort);
    }, "不支持从当前位置续播");
    require(test_host::seek_positions.empty() && test_host::media_opens == 3, "nonseekable fallback silently restarted");
    ++count;

    quality_fixture("run");
    {
        abort_callback abort;
        input_lxmusic input;
        require(decode_operation(input, abort, 0) == "error", "conversion runtime error was swallowed");
        require(test_host::resolve_calls == 1 && test_host::callbacks.empty(), "conversion changed quality midstream or stopped playback");
    }
    ++count;

    test_host::reset(test_host::failure::none);
    {
        abort_callback abort;
        input_lxmusic input;
        open_playback(input, abort);
        file_info info;
        input.get_info(info, abort);
        require_info(info, "codec", "FLAC");
        require_info(info, "samplerate", "44100");
        require_info(info, "bitrate", "320");
        require(info.meta["TITLE"] == "测试歌曲", "decoder replaced catalog metadata");
        info.info_set("codec", "LXMUSIC");
        double delta = 99;
        require(input.decode_get_dynamic_info(info, delta) && delta == 0, "initial stream information missing");
        require_info(info, "codec", "FLAC");
        require(!input.decode_get_dynamic_info(info, delta), "unchanged technical data repeatedly signaled");
        test_host::dynamic_bitrate = true;
        require(input.decode_get_dynamic_info(info, delta) && delta == -0.01, "VBR change was suppressed");
        require_info(info, "bitrate", "287");
        file_info cached;
        input.get_info(cached, abort);
        require_info(cached, "bitrate", "287");
        test_host::dynamic_bitrate = false;
        test_host::run_chunks = 1;
        audio_chunk chunk;
        require(input.decode_run(chunk, abort), "sample-rate fixture chunk missing");
        require(input.decode_get_dynamic_info(info, delta), "sample rate learned from first chunk was not published");
        require_info(info, "samplerate", "48000");
        require_info(info, "channels", "2");
        require_info(info, "bitrate", "287");
        require(info.meta["TITLE"] == "测试歌曲", "dynamic info replaced catalog metadata");
    }
    ++count;
    return count;
}

int main() {
    try {
        int count = 0;
        for (auto mode : { test_host::failure::resolve, test_host::failure::empty_url,
            test_host::failure::open, test_host::failure::initialize, test_host::failure::run, test_host::failure::seek }) {
            test_playback_failure(mode); ++count;
            test_playback_failure(mode, true); ++count;
            test_host::reset(mode);
            abort_callback abort;
            input_lxmusic input;
            require(decode_operation(input, abort, 0) == "error", "non-playback error was swallowed");
            require(test_host::callbacks.empty() && test_host::stops == 0, "scan/conversion affected playback");
            ++count;
        }
        test_playback_failure(test_host::failure::none, false, true); ++count;
        for (auto mode : { test_host::failure::none, test_host::failure::cancelled }) {
            test_host::reset(mode);
            abort_callback abort;
            input_lxmusic input;
            require(decode_operation(input, abort, input_flag_playback) == (mode == test_host::failure::none ? "eof" : "aborted"),
                "normal EOF or cancellation changed behavior");
            require(test_host::callbacks.empty() && test_host::popup_text.empty(), "EOF or cancellation reported a failure");
            ++count;
        }
        const int playback_count = count;
        for (bool seekable : { true, false }) {
            for (size_t read_limit : { SIZE_MAX, size_t(1) }) {
                test_stream_type("fLaC_example_audio", "audio/x-ogg", "audio/flac", seekable, read_limit); ++count;
                test_stream_type("OggS_example_audio", "audio/flac", "audio/ogg", seekable, read_limit); ++count;
                test_stream_type("RIFFsizeWAVEfmt ", "application/octet-stream", "audio/wav", seekable, read_limit); ++count;
                test_stream_type("RF64sizeWAVEfmt ", "audio/x-ogg", "audio/wav", seekable, read_limit); ++count;
                test_stream_type("sizeftypM4A ", "audio/x-ogg", "audio/mp4", seekable, read_limit); ++count;
                test_stream_type("ID3_example_tag", "audio/mpeg", "audio/mpeg", seekable, read_limit); ++count;
                test_stream_type("RIFFsizeAVI ", "video/avi", "video/avi", seekable, read_limit); ++count;
                test_stream_type("fLa", "audio/x-ogg", "audio/x-ogg", seekable, read_limit); ++count;
                test_stream_type("", "audio/mpeg", "audio/mpeg", seekable, read_limit); ++count;
                test_stream_type("{\"error\":\"expired\"}", "application/json", "application/json", seekable, read_limit); ++count;
            }
        }
        test_host::reset(test_host::failure::none);
        abort_callback abort;
        input_lxmusic input;
        require(decode_operation(input, abort, input_flag_playback) == "eof", "corrected FLAC failed to open");
        require(test_host::decoder_mime == "audio/flac", "real input class lost corrected MIME");
        require(test_host::decoder_prefix == test_host::stream_bytes.substr(0, 16), "decoder did not receive byte zero");
        ++count;
        const int quality_count = test_quality_and_info();
        for (const auto flags : {0u, input_flag_playback}) {
            test_host::reset(test_host::failure::resolve);
            test_host::navigation = true;
            abort_callback navigation_abort;
            input_lxmusic navigation_input;
            require(decode_operation(navigation_input, navigation_abort, flags) == "eof", "navigation row did not end without playback");
            navigation_input.decode_seek(0, navigation_abort);
            require(test_host::resolve_calls == 0 && test_host::stops == 0 && test_host::popup_text.empty(), "navigation row caused playback activity");
        }
        std::cout << playback_count << " playback, " << count - playback_count << " stream, " << quality_count << " quality/info, and 2 navigation regression cases passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
