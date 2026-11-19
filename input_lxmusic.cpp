#include "stdafx.h"

namespace {

class input_lxmusic : public input_stubs {
public:
    void open(service_ptr_t<file>, const char* path, t_input_open_reason reason, abort_callback&) {
        if (reason == input_open_info_write) throw exception_tagging_unsupported();
        if (!lxmusic::uri_to_payload(path, m_encoded, &m_payload)) throw exception_io_unsupported_format();
    }

    void get_info(file_info& info, abort_callback&) {
        lxmusic::payload_to_info(m_payload, info);
    }

    t_filestats2 get_stats2(unsigned, abort_callback&) { return filestats2_invalid; }

    void decode_initialize(unsigned flags, abort_callback& abort) {
        m_playback = (flags & input_flag_playback) != 0;
        try {
            initialize(flags, abort);
        } catch (const exception_aborted&) {
            throw;
        } catch (const std::exception& ex) {
            if (m_playback) stop_with_error(ex.what(), abort);
            throw;
        }
    }

    void initialize(unsigned flags, abort_callback& abort) {
        if (!m_payload.is_track()) throw exception_io_data("请选中专辑、歌手或歌单后按回车打开，再选择歌曲播放。");
        const auto quality = m_payload.get(lxmusic::pf_preferred_quality).empty()
            ? std::string("flac") : m_payload.get(lxmusic::pf_preferred_quality);
        const auto result = lxmusic::run_bridge({ "resolve", m_encoded, quality }, abort);
        std::string url;
        for (const auto& line : result.lines) {
            if (line.fields.size() >= 2 && line.fields[0] == "URL") {
                url = lxmusic::base64url_decode(line.fields[1]);
                break;
            }
        }
        if (url.empty()) throw exception_io_data("音源没有返回可播放地址");
        auto stream = lxmusic::open_audio_stream(url, abort);
        input_entry::g_open_for_decoding(m_decoder, stream, url.c_str(), abort, true);
        m_decoder->initialize(0, flags, abort);
    }

    bool decode_run(audio_chunk& chunk, abort_callback& abort) {
        try {
            return m_decoder->run(chunk, abort);
        } catch (const exception_aborted&) {
            throw;
        } catch (const std::exception& ex) {
            if (m_playback) stop_with_error(ex.what(), abort);
            throw;
        }
    }

    void decode_seek(double seconds, abort_callback& abort) {
        try {
            m_decoder->seek(seconds, abort);
        } catch (const exception_aborted&) {
            throw;
        } catch (const std::exception& ex) {
            if (m_playback) stop_with_error(ex.what(), abort);
            throw;
        }
    }
    bool decode_can_seek() { return m_decoder.is_valid() && m_decoder->can_seek(); }

    bool decode_get_dynamic_info(file_info& out, double& delta) {
        return m_decoder.is_valid() && m_decoder->get_dynamic_info(out, delta);
    }

    bool decode_get_dynamic_info_track(file_info& out, double& delta) {
        return m_decoder.is_valid() && m_decoder->get_dynamic_info_track(out, delta);
    }

    void decode_on_idle(abort_callback& abort) {
        if (m_decoder.is_valid()) m_decoder->on_idle(abort);
    }

    void retag(const file_info&, abort_callback&) { throw exception_tagging_unsupported(); }
    void remove_tags(abort_callback&) { throw exception_tagging_unsupported(); }

    static bool g_is_our_content_type(const char*) { return false; }
    static bool g_is_our_path(const char* path, const char* extension) {
        return (extension && _stricmp(extension, "lxmusic") == 0) ||
            (path && _strnicmp(path, lxmusic::kSchemePrefix, strlen(lxmusic::kSchemePrefix)) == 0);
    }
    static const char* g_get_name() { return "LX Music online item"; }
    static const GUID g_get_guid() {
        static const GUID value = { 0x92dcdf77, 0x3ea6, 0x4f8a,{ 0x9f, 0x63, 0x5b, 0x42, 0xac, 0x31, 0xb7, 0x12 } };
        return value;
    }

private:
    [[noreturn]] void stop_with_error(const char* reason, abort_callback& abort) {
        abort.check();
        std::string message = m_payload.get(lxmusic::pf_title);
        const auto& artist = m_payload.get(lxmusic::pf_artist);
        if (!artist.empty()) message += " — " + artist;
        message += "\n\n";
        message += reason;
        message += "\n\n已停止播放。可切换音质后重试，或检查音源。";
        // Keep the decoder pending until stop() cancels it. Throwing a normal
        // decode error here would let the host skip to the next playlist item
        // before the main-thread callback runs. The cloned abort token also
        // suppresses stale errors after the user stops or starts another song.
        fb2k::inMainThread([message] {
            playback_control::get()->stop();
            popup_message::g_complain("LX 播放失败", message.c_str());
        }, abort);
        abort.sleep(-1);
        throw exception_aborted();
    }

    std::string m_encoded;
    lxmusic::Payload m_payload;
    input_decoder::ptr m_decoder;
    bool m_playback = false;
};

static input_singletrack_factory_t<input_lxmusic> g_input_lxmusic_factory;

} // namespace
