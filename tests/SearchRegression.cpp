static int checks;
static void check(bool value, const char* message) { ++checks; if (!value) throw std::runtime_error(message); }
template<typename F> void rejects(F action, const char* message) {
    bool failed = false; try { action(); } catch (...) { failed = true; } check(failed, message);
}
int main() {
    using namespace lxmusic;
    try {
        SearchOptions options;
        options.source = "1234567890abcdef1234567890abcdef";
        options.site = "qq"; options.type = "album"; options.keyword = "张学友 \"live\"\\演唱会"; options.quality = "flac";
        SearchPage page; page.options = options; page.total_pages = 3; page.total_items = 65; page.has_next = true;
        auto encoded = encode_search_state(page);
        auto decoded = decode_search_state(encoded);
        check(decoded.options.keyword == options.keyword && decoded.options.source == options.source && decoded.options.type == "album", "search state lost Unicode/quotes/source/type");
        check(decoded.page == 1 && decoded.total_pages == 3 && decoded.total_items == 65 && decoded.has_next, "search state lost pagination");
        auto browse = options;
        browse.type = "playlist";
        browse.keyword.clear();
        page.options = browse;
        check(decode_search_state(encode_search_state(page)).options.keyword.empty(),
            "browse page lost empty keyword");
        browse.type = "rankings";
        page.options = browse;
        check(decode_search_state(encode_search_state(page)).options.type == "rankings",
            "ranking browse type was rejected");
        browse.type = "categories";
        page.options = browse;
        check(decode_search_state(encode_search_state(page)).options.type == "categories",
            "category browse type was rejected");
        browse.type = "featured";
        page.options = browse;
        check(decode_search_state(encode_search_state(page)).options.type == "featured",
            "featured playlist browse type was rejected");
        browse.type = "discover";
        page.options = browse;
        check(decode_search_state(encode_search_state(page)).options.type == "discover",
            "discover browse type was rejected");
        browse.type = "track";
        page.options = browse;
        rejects([&] { encode_search_state(page); }, "empty keyword accepted for track search");
        page.options = options;
        rejects([&] { decode_search_state("broken"); }, "malformed state accepted");
        rejects([&] { decode_search_state(base64url_encode_text(base64url_decode(encoded) + " trailing")); }, "trailing state accepted");
        auto bad = page; bad.options.source = "bad id";
        rejects([&] { encode_search_state(bad); }, "invalid source ID accepted");
        bad = page; bad.options.type = "shell";
        rejects([&] { encode_search_state(bad); }, "unknown search type accepted");
        bad = page; bad.page = 4;
        rejects([&] { encode_search_state(bad); }, "out-of-range page accepted");
        BridgeResult result; result.lines = {{{"PAGE","2","30","3","65","1"}}};
        check(search_page_from_bridge(result, options, 2).page == 2, "page metadata not parsed");
        rejects([&] { search_page_from_bridge(result, options, 1); }, "wrong response page accepted");
        result.lines.push_back(result.lines[0]);
        rejects([&] { search_page_from_bridge(result, options, 2); }, "duplicate metadata accepted");
        result.lines.resize(1); result.lines[0].fields[3] = "-1"; result.lines[0].fields[4] = "-1";
        check(search_page_from_bridge(result, options, 2).total_pages == -1, "unknown total was rejected");
        result.lines[0].fields[2] = "100";
        rejects([&] { search_page_from_bridge(result, options, 2); }, "wrong page size accepted");
        result.lines.clear();
        rejects([&] { search_page_from_bridge(result, options, 2); }, "missing metadata accepted");
        std::vector<std::string> items{"song-placeholder"};
        append_search_controls(items, page);
        check(items.size() == 4 && items[0] == "song-placeholder", "pagination changed song rows");
        auto next = decode_payload(items[1]), last = decode_payload(items[2]), jump = decode_payload(items[3]);
        check(next.get(pf_title) == "下一页 2" && next.get(pf_id) == "2", "first page has wrong next action");
        check(last.get(pf_title) == "到尾页 3" && last.get(pf_id) == "last", "last-page action missing");
        check(jump.get(pf_title).find("当前第1页  共3页") != std::string::npos && jump.get(pf_id) == "jump", "current/total page label missing");
        check(next.is_navigation() && !next.is_track() && !next.is_collection(), "navigation row masquerades as playable content");
        check(decode_search_state(next.get(pf_music_info_json)).options.source == options.source, "page action lost pinned source");
        page.page = 3; page.has_next = false; items.clear(); append_search_controls(items, page);
        check(items.size() == 2 && decode_payload(items[0]).get(pf_title) == "上一页 2", "last page offered next/last action");
        page.page = 2; page.has_next = true; page.total_pages = page.total_items = -1;
        items.clear(); append_search_controls(items, page);
        check(items.size() == 3 && decode_payload(items.back()).get(pf_title).find("总页数未知") != std::string::npos, "unknown total invented a last page");
        page.page = 1; page.total_pages = 1; page.total_items = 1; page.has_next = false;
        items = {"song-placeholder"}; append_search_controls(items, page);
        check(items == std::vector<std::string>{"song-placeholder"}, "single-page results retained a redundant page-jump row");
        page.total_pages = page.total_items = -1; page.has_next = true;
        items.clear(); append_search_controls(items, page);
        check(items.size() == 2 && decode_payload(items[0]).get(pf_id) == "2" &&
            decode_payload(items[1]).get(pf_id) == "jump", "unknown page count lost next/jump controls");
        int number = 9;
        check(parse_search_page_number("3", 3, number) && number == 3, "valid jump rejected");
        for (const auto& value : {"", "0", "-1", "4", "1x", "1.5", "2147483647"})
            check(!parse_search_page_number(value, 3, number) && number == 3, "invalid jump changed destination");
        check(parse_search_page_number("200", -1, number) && number == 200, "unknown total blocked a valid jump");
        int probes = 0;
        check(find_last_search_page(1, 94, [&](int p) { ++probes; return p <= 66; }) == 66 && probes <= 8,
            "inflated provider total did not find the last available page");
        probes = 0;
        check(find_last_search_page(1, 20, [&](int p) { ++probes; return p <= 20; }) == 20 && probes == 1,
            "valid last page caused unnecessary search requests");
        check(find_last_search_page(3, 94, [](int p) { return p <= 3; }) == 3, "last-page search lost the current page");
        rejects([&] { find_last_search_page(1, -1, [](int) { return true; }); }, "unknown total triggered last-page scan");
        std::string history;
        for (int i = 0; i < 25; ++i) { options.keyword = "搜索 " + std::to_string(i); history = update_search_history(history, options); }
        auto entries = decode_search_history(history);
        check(entries.size() == 20 && entries.front().keyword == "搜索 24" && entries.back().keyword == "搜索 5", "history limit/order incorrect");
        options.keyword = "搜索 10"; history = update_search_history(history, options); entries = decode_search_history(history);
        check(entries.size() == 20 && entries.front().keyword == options.keyword && entries[1].keyword == "搜索 24", "duplicate history was not moved to the front");
        options.source = "abcdef1234567890abcdef1234567890"; history = update_search_history(history, options); entries = decode_search_history(history);
        check(entries[0].source != entries[1].source && entries[0].keyword == entries[1].keyword, "different sources merged history");
        check(decode_search_history("broken\n" + history).size() == 20, "corrupt history discarded valid entries");
        check(decode_search_history(std::string(600000, 'x')).empty(), "oversized history was processed");
        Payload collection;
        collection.fields.resize(pf_count);
        collection.fields[pf_kind] = "album";
        collection.fields[pf_title] = "专辑搜索结果";
        collection.fields[pf_kind] = "playlist";
        collection.fields[pf_collection_kind] = "Ranking";
        check(is_ranking_payload(collection), "ranking payload was not recognized");
        collection.fields[pf_collection_kind] = "Playlist";
        check(!is_ranking_payload(collection), "ordinary playlist was mistaken for a ranking");
        collection.fields[pf_artist] = "测试用户 · 12 首";
        check(collection_result_label(collection) == "专辑搜索结果 — 测试用户 · 12 首",
            "ordinary playlist subtitle was not displayed");
        collection.fields[pf_version] = "1";
        collection.fields[pf_collection_kind] = "Ranking";
        collection.fields[pf_title] = "飙升榜";
        collection.fields[pf_artist] = "10,000 次收听";
        collection.fields[pf_album] = "QQ音乐";
        check(collection_result_label(collection) == "飙升榜", "browser ranking label included artist, platform or duration");
        collection.fields[pf_title] = "榜单 ? - 00";
        check(collection_result_label(collection) == "榜单 ? - 00", "browser removed legitimate title punctuation");
        auto ranking = encode_payload(collection.fields);
        check(has_collection_results({ranking}), "ranking did not open in the collection browser");
        Payload track;
        track.fields.resize(pf_count);
        track.fields[pf_version] = "1";
        track.fields[pf_kind] = "track";
        auto song = encode_payload(track.fields);
        check(!has_collection_results({song}), "track-only results did not stay in the native playlist");
        next.fields[pf_version] = "1";
        auto navigation = encode_payload(next.fields);
        check(!has_collection_results({song, navigation}), "song search pagination forced a collection browser");
        check(has_collection_results({ranking, navigation}), "collection pagination forced a native playlist");
        for (const auto kind : {"album", "artist", "playlist"}) {
            collection.fields[pf_kind] = kind;
            collection.fields[pf_collection_kind] = kind;
            check(has_collection_results({encode_payload(collection.fields)}), "collection search opened in native playlist");
        }
        collection.fields[pf_kind] = "album";
        collection.fields[pf_title] = "测试专辑";
        collection.fields[pf_artist] = "测试歌手";
        check(collection_result_label(collection) == "测试专辑 — 测试歌手", "album artist was not displayed");
        auto automatic = resolve_search_playlist_placement("LX QQ音乐 单曲 - 晴天", false, "");
        check(automatic.name == "LX QQ音乐 单曲 - 晴天" && automatic.create_new,
            "automatic search playlist placement was not preserved");
        auto fixed = resolve_search_playlist_placement("LX QQ音乐 单曲 - 晴天", true, "  固定搜索  ");
        check(fixed.name == "固定搜索" && !fixed.create_new,
            "fixed search playlist should replace the named destination");
        auto blankFixed = resolve_search_playlist_placement("LX QQ音乐 单曲 - 晴天", true, " \t ");
        check(blankFixed.name == "LX QQ音乐 单曲 - 晴天" && blankFixed.create_new,
            "blank fixed playlist name did not fall back safely");
        std::cout << checks << " search state/history regression checks passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
