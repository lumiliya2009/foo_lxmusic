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
        if (m_decoder.is_valid()) info.copy_info(m_stream_info);
    }

    t_filestats2 get_stats2(unsigned, abort_callback&) { return filestats2_invalid; }

    void decode_initialize(unsigned flags, abort_callback& abort) {
        m_playback = (flags & input_flag_playback) != 0;
        m_flags = flags;
        m_position = 0;
        m_tried.clear();
        m_urls.clear();
        m_resolve_calls = 0;
        m_more = false;
        m_last_media_error.clear();
        m_source.clear();
        try {
            initialize(abort);
        } catch (const exception_aborted&) {
            throw;
        } catch (const std::exception& ex) {
            if (m_playback) stop_with_error(ex.what(), abort);
            throw;
        }
    }

    void initialize(abort_callback& abort) {
        if (!m_payload.is_track()) throw exception_io_data("请选中专辑、歌手或歌单后按回车打开，再选择歌曲播放。");
        m_quality = m_payload.get(lxmusic::pf_preferred_quality).empty()
            ? std::string("flac") : m_payload.get(lxmusic::pf_preferred_quality);
        open_next_quality(abort, 0);
    }

    bool decode_run(audio_chunk& chunk, abort_callback& abort) {
        try {
            for (;;) {
                try {
                    const bool have_chunk = m_decoder->run(chunk, abort);
                    if (have_chunk) {
                        m_position += chunk.get_duration();
                        // Some decoders only know the rate after the first frame.
                        update_chunk_info("samplerate", chunk.get_sample_rate());
                        update_chunk_info("channels", chunk.get_channels());
                    }
                    return have_chunk;
                } catch (const exception_aborted&) { throw; }
                catch (const std::exception& ex) {
                    if (!m_playback || !can_retry(ex)) throw;
                    m_last_media_error = ex.what();
                    open_next_quality(abort, m_position);
                }
            }
        } catch (const exception_aborted&) {
            throw;
        } catch (const std::exception& ex) {
            if (m_playback) stop_with_error(ex.what(), abort);
            throw;
        }
    }

    void decode_seek(double seconds, abort_callback& abort) {
        try {
            try {
                m_decoder->seek(seconds, abort);
            } catch (const exception_aborted&) { throw; }
            catch (const std::exception& ex) {
                if (!m_playback || !can_retry(ex)) throw;
                m_last_media_error = ex.what();
                open_next_quality(abort, seconds);
            }
            m_position = seconds;
        } catch (const exception_aborted&) {
            throw;
        } catch (const std::exception& ex) {
            if (m_playback) stop_with_error(ex.what(), abort);
            throw;
        }
    }
    bool decode_can_seek() { return m_decoder.is_valid() && m_decoder->can_seek(); }

    bool decode_get_dynamic_info(file_info& out, double& delta) {
        if (!m_decoder.is_valid()) return false;
        const bool initial = m_info_pending;
        if (initial) {
            out.copy_info(m_stream_info);
            delta = 0;
            m_info_pending = false;
        }
        const bool changed = m_decoder->get_dynamic_info(out, delta);
        if (changed) {
            for (t_size i = 0; i < out.info_get_count(); ++i)
                m_stream_info.info_set(out.info_enum_name(i), out.info_enum_value(i));
        }
        return initial || changed;
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
    static bool has_status(const std::string& message, const char* code) {
        size_t pos = 0;
        while ((pos = message.find(code, pos)) != std::string::npos) {
            const auto digit = [](char c) { return c >= '0' && c <= '9'; };
            if ((pos == 0 || !digit(message[pos - 1])) &&
                (pos + 3 == message.size() || !digit(message[pos + 3]))) return true;
            pos += 3;
        }
        return false;
    }

    bool can_retry(const std::exception& error) const {
        if (!m_more || m_resolve_calls >= 8) return false;
        const std::string message = error.what();
        // Authentication/rate limits require user intervention or waiting.
        // Retry media rejection and invalid files, never source credentials.
        if (has_status(message, "401") || has_status(message, "429")) return false;
        // Decoder modules can be built without C++ RTTI. A dynamic_cast on
        // their exception objects can itself throw "no RTTI data". Use the
        // exception handler's type matching instead; this runs inside a catch.
        try { throw; }
        catch (const exception_io_data&) { return true; }
        catch (...) {}
        return has_status(message, "403") || has_status(message, "404") || has_status(message, "410") ||
            has_status(message, "500") || has_status(message, "502") || has_status(message, "503");
    }

    void remember_quality(const std::string& quality) {
        // Only protocol quality IDs can be sent as command-line arguments.
        for (const char* known : { "128k", "320k", "flac", "hires", "flac24bit", "atmos", "master" }) {
            if (quality == known && std::find(m_tried.begin(), m_tried.end(), quality) == m_tried.end()) {
                m_tried.push_back(quality);
                return;
            }
        }
    }

    void open_next_quality(abort_callback& abort, double position) {
        for (;;) {
            abort.check();
            m_decoder.release();
            std::vector<std::string> args{ "resolve", m_encoded, m_quality };
            if (!m_tried.empty() || !m_source.empty()) {
                std::string excluded;
                for (const auto& q : m_tried) { if (!excluded.empty()) excluded += ','; excluded += q; }
                args.push_back(excluded.empty() ? "-" : excluded);
                if (!m_source.empty()) args.push_back(m_source);
            }
            const auto before = m_tried.size();
            m_more = false;
            ++m_resolve_calls;
            const auto result = lxmusic::run_bridge(args, abort, [this](const lxmusic::BridgeLine& line) {
                if (line.fields.size() >= 2 && line.fields[0] == "TRIED") remember_quality(line.fields[1]);
                if (line.fields.size() >= 2 && line.fields[0] == "SOURCEID") {
                    const auto& id = line.fields[1];
                    if (id.size() == 32 && id.find_first_not_of("0123456789abcdef") == std::string::npos) m_source = id;
                }
            });
            std::string url, actual = m_quality;
            for (const auto& line : result.lines) {
                if (line.fields.size() >= 2 && line.fields[0] == "URL") {
                    url = lxmusic::base64url_decode(line.fields[1]);
                    if (line.fields.size() >= 3) actual = line.fields[2];
                } else if (line.fields.size() >= 2 && line.fields[0] == "REMAINING") {
                    m_more = line.fields[1] != "0";
                }
            }
            remember_quality(actual);
            m_more = m_more && m_tried.size() > before && m_resolve_calls < 8;
            if (url.empty()) throw exception_io_data("音源没有返回可播放地址");
            try {
                // A source may return one expired/forbidden URL for every tier.
                // Do not request that same failed media URL again.
                if (std::find(m_urls.begin(), m_urls.end(), url) != m_urls.end())
                    throw exception_io_data("音源重复返回了不可用的播放地址");
                m_urls.push_back(url);
                auto stream = lxmusic::open_audio_stream(url, abort);
                input_entry::g_open_for_decoding(m_decoder, stream, url.c_str(), abort, true);
                m_decoder->initialize(0, m_flags, abort);
                if (position > 0) {
                    if (!m_decoder->can_seek()) throw exception_io_data("回退音质不支持从当前位置续播");
                    m_decoder->seek(position, abort);
                }
                m_stream_info.reset();
                m_decoder->get_info(0, m_stream_info, abort);
                m_stream_info.info_set("lx_playback_quality", actual.c_str());
                m_info_pending = true;
                return;
            } catch (const exception_aborted&) { throw; }
            catch (const std::exception& ex) {
                m_last_media_error = ex.what();
                if (!can_retry(ex)) throw;
            }
        }
    }

    void update_chunk_info(const char* name, unsigned value) {
        if (value == 0) return;
        const auto text = std::to_string(value);
        const auto old = m_stream_info.info_get(name);
        if (!old || text != old) {
            m_stream_info.info_set(name, text.c_str());
            m_info_pending = true;
        }
    }

    [[noreturn]] void stop_with_error(const char* reason, abort_callback& abort) {
        abort.check();
        std::string message = m_payload.get(lxmusic::pf_title);
        const auto& artist = m_payload.get(lxmusic::pf_artist);
        if (!artist.empty()) message += " — " + artist;
        message += "\n\n";
        message += reason;
        if (!m_last_media_error.empty() && m_last_media_error != reason) message += "\n" + m_last_media_error;
        if (!m_tried.empty()) {
            message += "\n已尝试音质：";
            for (size_t i = 0; i < m_tried.size(); ++i) { if (i) message += "、"; message += m_tried[i]; }
        }
        message += "\n\n已停止播放。请稍后重试或检查音源。";
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
    file_info_impl m_stream_info;
    std::vector<std::string> m_tried, m_urls;
    std::string m_quality, m_last_media_error, m_source;
    unsigned m_flags = 0, m_resolve_calls = 0;
    double m_position = 0;
    bool m_more = false, m_info_pending = false;
    bool m_playback = false;
};

static input_singletrack_factory_t<input_lxmusic> g_input_lxmusic_factory;

} // namespace
