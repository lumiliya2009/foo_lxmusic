#include "stdafx.h"

namespace lxmusic {
namespace {

const char* audio_content_type(const unsigned char* bytes, size_t size) {
    if (size >= 4 && memcmp(bytes, "fLaC", 4) == 0) return "audio/flac";
    if (size >= 4 && memcmp(bytes, "OggS", 4) == 0) return "audio/ogg";
    if (size >= 12 && (memcmp(bytes, "RIFF", 4) == 0 || memcmp(bytes, "RF64", 4) == 0) &&
        memcmp(bytes + 8, "WAVE", 4) == 0) return "audio/wav";
    if (size >= 12 && memcmp(bytes + 4, "ftyp", 4) == 0) return "audio/mp4";
    // ID3 alone does not identify MP3: other formats can also have an ID3 tag.
    // Leave unrecognized headers to the host's usual decoder selection.
    return nullptr;
}

class audio_type_file : public file_chain {
public:
    audio_type_file(file::ptr source, const char* type) : file_chain(source), m_type(type) {}
    bool get_content_type(pfc::string_base& out) override { out = m_type.c_str(); return true; }
private:
    std::string m_type;
};

} // namespace

file::ptr open_audio_stream(const std::string& url, abort_callback& abort) {
    file::ptr stream;
    filesystem::g_open_read(stream, url.c_str(), abort);
    // Some music CDNs send FLAC as audio/x-ogg. Supplying that MIME to the
    // input manager selects the wrong decoder. Prefer unambiguous file bytes
    // over the response header while keeping the original URL and HTTP stream.
    if (stream->can_seek()) stream = file_cached::g_create(stream, abort, 4096);
    unsigned char prefix[16]{};
    size_t count = 0;
    while (count < sizeof(prefix)) {
        abort.check();
        const auto read = stream->read(prefix + count, sizeof(prefix) - count, abort);
        if (read == 0) break;
        count += read;
    }
    // Decoders must see byte zero, including for streams without seek support.
    stream->reopen(abort);
    if (const auto type = audio_content_type(prefix, count))
        return fb2k::service_new<audio_type_file>(stream, type);
    return stream;
}

} // namespace lxmusic
