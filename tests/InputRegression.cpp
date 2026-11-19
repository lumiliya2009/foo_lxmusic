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
        std::cout << playback_count << " playback regression cases and " << count - playback_count << " stream regression cases passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
