#include "stdafx.h"
#include <charconv>
#include <iomanip>
#include <limits>
#include <sstream>

namespace lxmusic {
namespace {
constexpr int max_page = (std::numeric_limits<int>::max)() / 50;
int integer(const std::string& text) {
    int value = 0;
    const auto parsed = std::from_chars(text.data(), text.data() + text.size(), value);
    if (text.empty() || parsed.ec != std::errc() || parsed.ptr != text.data() + text.size())
        throw std::runtime_error("搜索分页信息无效");
    return value;
}
void validate(const SearchPage& value) {
    const auto& o = value.options;
    if (o.keyword.empty() || o.keyword.size() > 4096 || o.source.size() != 32 ||
        o.source.find_first_not_of("0123456789abcdef") != std::string::npos ||
        o.site.empty() || o.site.size() > 32 || o.site.find_first_not_of("abcdefghijklmnopqrstuvwxyz0123456789_-") != std::string::npos ||
        (o.type != "track" && o.type != "album" && o.type != "artist" && o.type != "playlist") ||
        o.quality.empty() || o.quality.size() > 32 || o.quality.find_first_not_of("abcdefghijklmnopqrstuvwxyz0123456789_") != std::string::npos ||
        value.page < 1 || value.page > max_page || value.limit < 1 || value.limit > 50 ||
        (value.total_pages != -1 && value.total_pages < 1) || value.total_items < -1 ||
        (value.total_pages > 0 && value.page > value.total_pages))
        throw std::runtime_error("搜索分页信息无效");
}
bool same_search(const SearchOptions& a, const SearchOptions& b) {
    return a.keyword == b.keyword && a.source == b.source && a.site == b.site && a.type == b.type && a.quality == b.quality;
}
}

std::string encode_search_state(const SearchPage& page) {
    validate(page);
    std::ostringstream out;
    const auto& o = page.options;
    out << 1 << ' ' << std::quoted(o.source) << ' ' << std::quoted(o.site) << ' ' << std::quoted(o.type)
        << ' ' << std::quoted(o.keyword) << ' ' << std::quoted(o.quality) << ' ' << page.page << ' ' << page.limit
        << ' ' << page.total_pages << ' ' << page.total_items << ' ' << (page.has_next ? 1 : 0);
    return base64url_encode_text(out.str());
}

SearchPage decode_search_state(const std::string& encoded) {
    if (encoded.size() > 16384) throw std::runtime_error("搜索记录过长");
    std::istringstream in(base64url_decode(encoded));
    SearchPage page;
    auto& o = page.options;
    int version = 0, next = 0;
    if (!(in >> version >> std::quoted(o.source) >> std::quoted(o.site) >> std::quoted(o.type)
        >> std::quoted(o.keyword) >> std::quoted(o.quality) >> page.page >> page.limit
        >> page.total_pages >> page.total_items >> next) || version != 1 || (next != 0 && next != 1))
        throw std::runtime_error("搜索记录无效");
    in >> std::ws;
    if (!in.eof()) throw std::runtime_error("搜索记录包含多余数据");
    page.has_next = next != 0;
    validate(page);
    return page;
}

SearchPage search_page_from_bridge(const BridgeResult& result, const SearchOptions& options, int requested) {
    SearchPage page;
    page.options = options;
    bool found = false;
    for (const auto& line : result.lines) {
        const auto& fields = line.fields;
        if (fields.empty() || fields[0] != "PAGE") continue;
        if (found || fields.size() != 6) throw std::runtime_error("搜索分页信息无效");
        found = true;
        page.page = integer(fields[1]); page.limit = integer(fields[2]);
        page.total_pages = integer(fields[3]); page.total_items = integer(fields[4]);
        if (fields[5] != "0" && fields[5] != "1") throw std::runtime_error("搜索分页信息无效");
        page.has_next = fields[5] == "1";
    }
    if (!found || page.page != requested || page.limit != 30) throw std::runtime_error("搜索没有返回正确的分页信息");
    validate(page);
    return page;
}

std::string search_page_label(const SearchPage& page) {
    return "当前第" + std::to_string(page.page) + "页  " +
        (page.total_pages > 0 ? "共" + std::to_string(page.total_pages) + "页" : "总页数未知");
}

void append_search_controls(std::vector<std::string>& items, const SearchPage& page) {
    const auto state = encode_search_state(page);
    const auto add = [&](const std::string& action, const std::string& title) {
        std::vector<std::string> fields(pf_count);
        fields[pf_version] = "1"; fields[pf_kind] = "search-page";
        fields[pf_platform_id] = page.options.site; fields[pf_lx_source_id] = page.options.source;
        fields[pf_id] = action; fields[pf_title] = title;
        fields[pf_duration_ms] = "0"; fields[pf_music_info_json] = state;
        items.push_back(encode_payload(fields));
    };
    if (page.page > 1) add(std::to_string(page.page - 1), "上一页 " + std::to_string(page.page - 1));
    if (page.has_next && page.page < max_page) add(std::to_string(page.page + 1), "下一页 " + std::to_string(page.page + 1));
    if (page.total_pages > page.page && page.total_pages <= max_page)
        add("last", "到尾页 " + std::to_string(page.total_pages));
    add("jump", "页数跳转  " + search_page_label(page));
}

bool parse_search_page_number(const std::string& text, int maximum, int& page) {
    try {
        const auto value = integer(text);
        if (value < 1 || value > max_page || (maximum > 0 && value > maximum)) return false;
        page = value;
        return true;
    } catch (...) { return false; }
}

int find_last_search_page(int current, int advertised, const std::function<bool(int)>& has_items) {
    if (current < 1 || advertised < current || advertised > max_page) throw std::runtime_error("尾页页码无效");
    if (has_items(advertised)) return advertised;
    // Some catalogs advertise more results than their public API exposes.
    // Find the last accessible page without downloading every intermediate page.
    int low = current, high = advertised - 1;
    while (low < high) {
        const int middle = low + (high - low + 1) / 2;
        if (has_items(middle)) low = middle;
        else high = middle - 1;
    }
    return low;
}

std::vector<SearchOptions> decode_search_history(const std::string& data) {
    std::vector<SearchOptions> result;
    if (data.size() > 512 * 1024) return result;
    std::istringstream in(data);
    std::string record;
    while (result.size() < 20 && std::getline(in, record)) {
        try {
            auto options = decode_search_state(record).options;
            if (std::none_of(result.begin(), result.end(), [&](const auto& item) { return same_search(item, options); }))
                result.push_back(std::move(options));
        } catch (...) {}
    }
    return result;
}

std::string update_search_history(const std::string& data, const SearchOptions& options) {
    auto history = decode_search_history(data);
    history.erase(std::remove_if(history.begin(), history.end(), [&](const auto& item) { return same_search(item, options); }), history.end());
    history.insert(history.begin(), options);
    if (history.size() > 20) history.resize(20);
    std::string result;
    for (const auto& item : history) { SearchPage page; page.options = item; result += encode_search_state(page) + '\n'; }
    return result;
}
} // namespace lxmusic
