#include "stdafx.h"

DECLARE_COMPONENT_VERSION(
    "LX Online Music",
    "0.1.8",
    "导入 LX Music 自定义音源，在 foobar2000 中搜索、播放与批量下载在线歌曲，并获取歌词。\n"
    "支持 foobar2000 v1 x86 与 v2 x64。\n\n"
    "请仅在获得授权并遵守相应服务条款的前提下使用在线内容。"
);

VALIDATE_COMPONENT_FILENAME("foo_lxmusic.dll");
FOOBAR2000_IMPLEMENT_CFG_VAR_DOWNGRADE;

namespace {

class lxmusic_init : public initquit {
public:
    void on_init() override {
        lxmusic::refresh_playlist_payload_info();
    }
};

static initquit_factory_t<lxmusic_init> g_lxmusic_init;

} // namespace
