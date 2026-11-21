#include "../stdafx.h"
#include <fstream>

// Loaded only by run-display-integration.ps1 in an isolated portable host.
// Exercise the host's real title-formatting engine, not a substitute parser.
DECLARE_COMPONENT_VERSION("LX display regression", "test", "Isolated display regression fixture");
VALIDATE_COMPONENT_FILENAME("foo_lxmusic.dll");

namespace {
size_t checks = 0;
void check(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}
std::string render(const file_info& info, const char* script) {
    auto compiled = titleformat_compiler::get()->compile(script);
    check(compiled.is_valid(), "Column title format did not compile");
    pfc::string8 result;
    compiled->run_simple(make_playable_location("file://C:/lx-display-test.flac", 0), &info, result);
    return result.c_str();
}

class display_test : public initquit {
public:
    void on_init() override {
        wchar_t executable[32768]{};
        GetModuleFileNameW(nullptr, executable, 32768);
        std::wstring report_path(executable);
        report_path.resize(report_path.find_last_of(L"\\/") + 1);
        report_path += L"profile\\display-test.txt";
        std::ofstream(report_path.c_str()) << "INIT\n";
        fb2k::inMainThread([report_path] {
            std::string report;
            try { run(); report = "PASS " + std::to_string(checks) + " actual host display checks\n"; }
            catch (const std::exception& e) { report = std::string("FAIL ") + e.what() + "\n"; }
            std::ofstream(report_path.c_str()) << report;
            standard_commands::main_exit();
        });
    }
private:
    static void run() {
        using namespace lxmusic;
        std::vector<std::string> formats;
        service_enum_t<fb2k::playlistColumnProvider> services;
        fb2k::playlistColumnProvider::ptr provider;
        while (services.next(provider)) {
            if (provider->numColumns() != 4 ||
                strcmp(provider->columnName(0)->c_str(), "LX 艺术家/专辑") != 0) continue;
            for (size_t i = 0; i < 4; ++i) formats.emplace_back(provider->columnFormatSpec(i)->c_str());
        }
        check(formats.size() == 4, "LX columns were not registered");
        const char* baseline[] = { "%album artist% - %album%", "%tracknumber%",
            "%title%[ '//' %track artist%]", "%length%" };
        auto clean_row = [&](const Payload& payload) {
            file_info_impl info;
            // Simulate old/reused metadata, including the ineffective 0.1.14 workaround.
            info.meta_set("ARTIST", ""); info.meta_set("ALBUM", "");
            info.meta_set("ALBUM ARTIST", ""); info.meta_set("TRACKNUMBER", "");
            info.info_set("length", ""); info.set_length(123);
            payload_to_info(payload, info);
            for (size_t i = 0; i < formats.size(); ++i)
                check(render(info, formats[i].c_str()) == (i == 2 ? payload.get(pf_title) : ""),
                    "Name-only row retained a separator, number, unknown field or duration");
            check(!info.meta_exists("TRACKNUMBER"), "Old empty track number remained");
            check(!info.info_get("length"), "Old synthetic length remained");
        };
        Payload payload; payload.fields.resize(pf_count);
        payload.fields[pf_version] = "1";
        payload.fields[pf_kind] = "playlist";
        payload.fields[pf_collection_kind] = "Ranking";
        payload.fields[pf_title] = "巅峰榜·流行指数";
        payload.fields[pf_artist] = "19,100,000 次收听";
        payload.fields[pf_album] = "QQ音乐";
        clean_row(payload);
        // Literal punctuation in a real title must survive; do not strip '?' globally.
        payload.fields[pf_title] = "榜单 ? - 00";
        clean_row(payload);
        payload.fields[pf_collection_kind].clear();
        payload.fields[pf_kind] = "search-page";
        for (const auto title : { "上一页 1", "下一页 3", "到尾页 4", "页数跳转  当前第2页  共4页" }) {
            payload.fields[pf_title] = title;
            clean_row(payload);
        }
        payload.fields[pf_artist] = "测试艺术家";
        payload.fields[pf_album] = "测试专辑";
        payload.fields[pf_platform_label] = "QQ音乐";
        payload.fields[pf_duration_ms] = "245000";
        for (const auto kind : { "track", "album", "artist", "playlist" }) {
            payload.fields[pf_kind] = kind;
            payload.fields[pf_title] = "测试名称";
            file_info_impl info;
            info.meta_set("LX_TITLE_ONLY", "1");
            payload_to_info(payload, info);
            check(!info.meta_exists("LX_TITLE_ONLY"), "Name-only flag leaked to regular result");
            check(strcmp(info.meta_get("ARTIST", 0), "测试艺术家") == 0, "Artist was lost");
            check(info.meta_exists("ALBUM"), "Album was lost");
            for (size_t i = 0; i < formats.size(); ++i)
                check(render(info, formats[i].c_str()) == render(info, baseline[i]),
                    "Normal row formatting changed");
        }
        file_info_impl local;
        local.meta_set("TITLE", "本地歌曲 ?"); local.meta_set("ARTIST", "本地艺术家");
        local.meta_set("ALBUM ARTIST", "专辑艺术家"); local.meta_set("ALBUM", "本地专辑");
        local.meta_set("TRACKNUMBER", "7"); local.set_length(185);
        for (size_t i = 0; i < formats.size(); ++i)
            check(render(local, formats[i].c_str()) == render(local, baseline[i]), "Local song formatting changed");
        check(render(local, formats[1].c_str()) == "07", "Real track number lost its padding");
        check(render(local, formats[3].c_str()) == "3:05", "Real duration was hidden");
    }
};
static initquit_factory_t<display_test> g_display_test;
}
