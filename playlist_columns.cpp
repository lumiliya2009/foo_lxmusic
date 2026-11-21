#include "stdafx.h"

namespace lxmusic {
namespace {

struct playlist_column {
    GUID id;
    const char* name;
    const char* format;
    unsigned flags;
};

// Suppress the whole expression for catalog/navigation rows. Empty artist or
// track tags cannot remove literal separators, numeric padding or %length%.
const playlist_column columns[] = {
    { { 0x5e72df3c, 0x6134, 0x43de, { 0xa4, 0x83, 0x5f, 0xb9, 0x72, 0x39, 0x8e, 0xf1 } },
        "LX 艺术家/专辑", "$if(%lx_title_only%,,%album artist% - %album%)", 0 },
    { { 0x7b05a9e1, 0xa8ce, 0x44ab, { 0x88, 0x42, 0x21, 0x90, 0xdc, 0xc7, 0xf3, 0x4e } },
        "LX 音轨号", "$if(%lx_title_only%,,%tracknumber%)", fb2k::playlistColumnProvider::flag_alignRight },
    { { 0xc6eb524a, 0x9823, 0x44d1, { 0xa8, 0x05, 0x6e, 0xc8, 0x90, 0xbc, 0x2b, 0x33 } },
        "LX 标题", "$if(%lx_title_only%,%title%,%title%[ '//' %track artist%])", 0 },
    { { 0x8a4396d0, 0x8f0b, 0x4c31, { 0x91, 0xe2, 0x7d, 0xb8, 0x51, 0x35, 0x6a, 0x07 } },
        "LX 时长", "$if(%lx_title_only%,,%length%)", fb2k::playlistColumnProvider::flag_alignRight },
};

class lx_playlist_columns : public fb2k::playlistColumnProvider {
public:
    size_t numColumns() override { return std::size(columns); }
    GUID columnID(size_t index) override { return columns[index].id; }
    fb2k::stringRef columnName(size_t index) override { return fb2k::makeString(columns[index].name); }
    fb2k::stringRef columnFormatSpec(size_t index) override { return fb2k::makeString(columns[index].format); }
    fb2k::stringRef columnSortScript(size_t) override { return nullptr; }
    unsigned columnFlags(size_t index) override { return columns[index].flags; }
};
static service_factory_single_t<lx_playlist_columns> g_lx_playlist_columns;

} // namespace
} // namespace lxmusic
