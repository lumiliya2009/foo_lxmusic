#include "stdafx.h"
#include <foobar2000/helpers/atl-misc.h>
#include <foobar2000/SDK/coreDarkMode.h>

namespace lxmusic {

namespace {

cfg_int g_search_site({ 0x628a3da1, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, 0);
cfg_int g_search_type({ 0x628a3da2, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, 0);
cfg_string g_search_keyword({ 0x628a3da3, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_download_folder({ 0x628a3da4, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_download_quality({ 0x628a3da5, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "flac");
cfg_string g_search_site_id({ 0x628a3da6, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_search_history({ 0x628a3da7, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_download_naming({ 0x628a3da8, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "{序号} - {曲名} - {艺术家}");
cfg_bool g_download_skip_failures({ 0x628a3da9, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, true);
cfg_bool g_download_cleanup({ 0x628a3daa, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, false);
cfg_int g_download_workers({ 0x628a3dab, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, 3);
cfg_bool g_download_overrides({ 0x628a3dac, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, true);
cfg_bool g_auto_updates({ 0x628a3dad, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, true);
cfg_bool g_apply_search_naming({ 0x628a3db0, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x13 } }, false);
const GUID guid_preferences = { 0x85d64b47, 0xc949, 0x409d,{ 0xa6, 0x8a, 0x5e, 0x31, 0xc5, 0x94, 0xef, 0x17 } };

std::string dialog_text(HWND dialog, UINT id) {
    pfc::string8 value;
    uGetDlgItemText(dialog, id, value);
    return value.c_str();
}

std::string default_download_folder() {
    wchar_t path[MAX_PATH]{};
    if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_MYMUSIC, nullptr, SHGFP_TYPE_CURRENT, path)))
        return pfc::stringcvt::string_utf8_from_wide(path).get_ptr();
    return {};
}

void fill_download_naming(HWND control, const std::string& current) {
    CComboBox naming(control);
    naming.LimitText(256);
    for (const auto value : { L"{序号} - {曲名} - {艺术家}", L"{序号} - {曲名}", L"{曲名} - {艺术家}",
        L"{艺术家} - {曲名}", L"{曲名}", L"{专辑名} - {序号} - {曲名}", L"{序号} - {曲名} - {艺术家} - {音质}" })
        naming.AddString(value);
    const pfc::stringcvt::string_wide_from_utf8 saved(current.c_str());
    if (naming.FindStringExact(-1, saved) == CB_ERR) naming.AddString(saved);
    naming.SetWindowText(saved);
}

const char* const search_types[] = { "track", "album", "artist", "playlist", "featured", "rankings", "categories" };
const wchar_t* const search_type_names[] = { L"单曲", L"专辑", L"歌手", L"歌单", L"精选歌单", L"榜单", L"歌单分类" };

int search_type_index(const std::string& type) {
    for (int index = 0; index < 7; ++index)
        if (type == search_types[index]) return index;
    return 0;
}

class search_dialog : public CDialogImpl<search_dialog> {
public:
    enum { IDD = IDD_LX_SEARCH };
    SearchOptions options;
    std::vector<SourceOption> sources;
    std::string preferred_site;
    std::vector<SearchOptions> history;
    int pending_history = -1;

    BEGIN_MSG_MAP(search_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_HANDLER(IDC_LX_SOURCE, CBN_SELCHANGE, OnSourceChanged)
        COMMAND_HANDLER(IDC_LX_SITE, CBN_SELCHANGE, OnSiteChanged)
        COMMAND_HANDLER(IDC_LX_KEYWORD, CBN_SELENDOK, OnHistoryChosen)
        COMMAND_HANDLER(IDC_LX_KEYWORD, CBN_SETFOCUS, OnKeywordFocus)
        MESSAGE_HANDLER(WM_APP + 17, OnSelectKeyword)
        MESSAGE_HANDLER(WM_APP + 18, OnApplyHistory)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()

    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        auto source = CComboBox(GetDlgItem(IDC_LX_SOURCE));
        int selected = 0;
        for (size_t i = 0; i < sources.size(); ++i) {
            source.AddString(pfc::stringcvt::string_wide_from_utf8(sources[i].name.c_str()));
            if (sources[i].active) selected = static_cast<int>(i);
        }
        source.SetCurSel(selected);
        FillSites();

        history = decode_search_history(g_search_history.get().c_str());
        auto keyword = CComboBox(GetDlgItem(IDC_LX_KEYWORD));
        keyword.LimitText(1024);
        for (const auto& entry : history) {
            std::string label = entry.site;
            for (const auto& saved_source : sources) for (const auto& site : saved_source.sites)
                if (site.id == entry.site) label = site.label;
            const auto type = pfc::stringcvt::string_utf8_from_wide(search_type_names[search_type_index(entry.type)]);
            label = (entry.keyword.empty() ? "浏览" : entry.keyword) + "（" + label + " / " + type.get_ptr() + "）";
            keyword.AddString(pfc::stringcvt::string_wide_from_utf8(label.c_str()));
        }
        uSetDlgItemText(*this, IDC_LX_KEYWORD, g_search_keyword.get().c_str());
        keyword.SetEditSel(0, -1);
        GetDlgItem(IDC_LX_KEYWORD).SetFocus();
        return FALSE;
    }

    const SourceOption* SelectedSource() const {
        const int i = CComboBox(GetDlgItem(IDC_LX_SOURCE)).GetCurSel();
        return i >= 0 && static_cast<size_t>(i) < sources.size() ? &sources[i] : nullptr;
    }

    const SourceSite* SelectedSite() const {
        const auto source = SelectedSource();
        const int i = CComboBox(GetDlgItem(IDC_LX_SITE)).GetCurSel();
        return source && i >= 0 && static_cast<size_t>(i) < source->sites.size() ? &source->sites[i] : nullptr;
    }

    void FillSites() {
        auto site = CComboBox(GetDlgItem(IDC_LX_SITE));
        std::string preferred = preferred_site.empty() ? g_search_site_id.get().c_str() : preferred_site;
        if (preferred.empty()) {
            static const char* legacy[] = { "qq", "kugou", "netease" };
            preferred = legacy[std::clamp(static_cast<int>(g_search_site), 0, 2)];
        }
        site.ResetContent();
        int selected = 0;
        if (const auto source = SelectedSource()) {
            for (size_t i = 0; i < source->sites.size(); ++i) {
                site.AddString(pfc::stringcvt::string_wide_from_utf8(source->sites[i].label.c_str()));
                if (source->sites[i].id == preferred) selected = static_cast<int>(i);
            }
        }
        site.SetCurSel(selected);
        FillTypes();
    }

    void FillTypes() {
        auto type = CComboBox(GetDlgItem(IDC_LX_TYPE));
        type.ResetContent();
        const auto site = SelectedSite();
        const auto add = [&](int index) {
            const int row = type.AddString(search_type_names[index]);
            type.SetItemData(row, static_cast<DWORD_PTR>(index));
        };
        add(0);
        int selected_row = 0;
        const auto add_collection = [&](int index) {
            add(index);
            if (static_cast<int>(g_search_type) == index) selected_row = type.GetCount() - 1;
        };
        if (site && site->collections) { add_collection(1); add_collection(2); add_collection(3); }
        if (site && site->featured) add_collection(4);
        if (site && site->rankings) add_collection(5);
        if (site && site->categories) add_collection(6);
        type.SetCurSel(selected_row);
        GetDlgItem(IDOK).EnableWindow(site != nullptr && site->searchable);
        uSetDlgItemText(*this, IDC_LX_STATUS, !site || !site->searchable ? "" :
            site->collections || site->rankings || site->categories ? "可搜索，也可浏览精选歌单、榜单和歌单分类。" : "此站点支持单曲搜索，结果中按回车播放。" );
        if (!site || !site->searchable)
            MessageBoxW(!site ? L"当前音源没有可搜索的站点，请选择其他音源。" :
                L"音源支持此站点的播放解析，但未提供搜索接口。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
    }
    LRESULT OnSourceChanged(WORD, WORD, HWND, BOOL&) { FillSites(); return 0; }
    LRESULT OnSiteChanged(WORD, WORD, HWND, BOOL&) {
        if (const auto site = SelectedSite()) preferred_site = site->id;
        FillTypes(); return 0;
    }

    LRESULT OnKeywordFocus(WORD, WORD, HWND, BOOL&) { PostMessage(WM_APP + 17); return 0; }
    LRESULT OnSelectKeyword(UINT, WPARAM, LPARAM, BOOL&) {
        const auto keyword = GetDlgItem(IDC_LX_KEYWORD);
        if (::GetFocus() == keyword || ::IsChild(keyword, ::GetFocus())) CComboBox(keyword).SetEditSel(0, -1);
        return 0;
    }
    LRESULT OnHistoryChosen(WORD, WORD, HWND, BOOL&) {
        pending_history = CComboBox(GetDlgItem(IDC_LX_KEYWORD)).GetCurSel();
        PostMessage(WM_APP + 18);
        return 0;
    }
    LRESULT OnApplyHistory(UINT, WPARAM, LPARAM, BOOL&) {
        const int selected = pending_history;
        pending_history = -1;
        if (selected < 0 || static_cast<size_t>(selected) >= history.size()) return 0;
        const auto& entry = history[selected];
        for (size_t i = 0; i < sources.size(); ++i)
            if (sources[i].id == entry.source) CComboBox(GetDlgItem(IDC_LX_SOURCE)).SetCurSel(static_cast<int>(i));
        preferred_site = entry.site;
        FillSites();
        auto types = CComboBox(GetDlgItem(IDC_LX_TYPE));
        int selected_type = 0;
        for (int row = 0; row < types.GetCount(); ++row)
            if (static_cast<int>(types.GetItemData(row)) == search_type_index(entry.type)) {
                selected_type = row;
                break;
            }
        types.SetCurSel(selected_type);
        uSetDlgItemText(*this, IDC_LX_KEYWORD, entry.keyword.c_str());
        CComboBox(GetDlgItem(IDC_LX_KEYWORD)).SetEditSel(0, -1);
        return 0;
    }

    LRESULT OnOK(WORD, WORD, HWND, BOOL&) {
        if (pending_history >= 0) { BOOL handled = FALSE; OnApplyHistory(0, 0, 0, handled); }
        options.keyword = dialog_text(*this, IDC_LX_KEYWORD);
        const auto first = options.keyword.find_first_not_of(" \t\r\n");
        options.keyword = first == std::string::npos ? std::string() :
            options.keyword.substr(first, options.keyword.find_last_not_of(" \t\r\n") - first + 1);
        const auto source = SelectedSource();
        const auto site = SelectedSite();
        if (!source || !site || !site->searchable) return 0;
        auto types = CComboBox(GetDlgItem(IDC_LX_TYPE));
        const int row = types.GetCurSel();
        const int type = row >= 0 ? static_cast<int>(types.GetItemData(row)) : 0;
        if (type < 0 || type > 6) return 0;
        const bool browse = type == 3 || type == 4 || type == 5 || type == 6;
        if (options.keyword.empty() && !browse) {
            MessageBoxW(L"请输入搜索关键词；歌单、榜单和分类可以留空浏览。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
            GetDlgItem(IDC_LX_KEYWORD).SetFocus();
            return 0;
        }
        options.source = source->id;
        options.site = site->id;
        options.type = search_types[type];
        options.quality = "flac";
        g_search_site_id = options.site.c_str();
        g_search_type = type;
        g_search_keyword = options.keyword.c_str();
        EndDialog(IDOK);
        return 0;
    }

    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
};

class browse_dialog : public CDialogImpl<browse_dialog> {
public:
    enum { IDD = IDD_LX_BROWSE };
    SearchOptions options;
    std::vector<SourceOption> sources;
    std::vector<size_t> visible_sites;

    BEGIN_MSG_MAP(browse_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_HANDLER(IDC_LX_SOURCE, CBN_SELCHANGE, OnSourceChanged)
        COMMAND_HANDLER(IDC_LX_SITE, CBN_SELCHANGE, OnSiteChanged)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()

    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        auto combo = CComboBox(GetDlgItem(IDC_LX_SOURCE));
        int selected = 0;
        for (size_t index = 0; index < sources.size(); ++index) {
            const auto supported = std::any_of(sources[index].sites.begin(), sources[index].sites.end(),
                [](const SourceSite& site) { return site.searchable && site.discover; });
            if (!supported) continue;
            const int row = combo.AddString(pfc::stringcvt::string_wide_from_utf8(sources[index].name.c_str()));
            combo.SetItemData(row, static_cast<DWORD_PTR>(index));
            if (sources[index].active) selected = row;
        }
        combo.SetCurSel(selected);
        FillSites();
        GetDlgItem(IDC_LX_SITE).SetFocus();
        return FALSE;
    }

    const SourceOption* SelectedSource() const {
        auto combo = CComboBox(GetDlgItem(IDC_LX_SOURCE));
        const int row = combo.GetCurSel();
        if (row < 0) return nullptr;
        const auto index = static_cast<size_t>(combo.GetItemData(row));
        return index < sources.size() ? &sources[index] : nullptr;
    }

    const SourceSite* SelectedSite() const {
        auto combo = CComboBox(GetDlgItem(IDC_LX_SITE));
        const int row = combo.GetCurSel();
        if (row < 0) return nullptr;
        const auto index = static_cast<size_t>(combo.GetItemData(row));
        return index < visible_sites.size() ? &SelectedSource()->sites[visible_sites[index]] : nullptr;
    }

    void FillSites() {
        visible_sites.clear();
        auto combo = CComboBox(GetDlgItem(IDC_LX_SITE));
        combo.ResetContent();
        const auto preferred = std::string(g_search_site_id.get().c_str());
        int selected = 0;
        if (const auto source = SelectedSource()) {
            for (size_t index = 0; index < source->sites.size(); ++index) {
                const auto& site = source->sites[index];
                if (!site.searchable || !site.discover) continue;
                const int row = combo.AddString(pfc::stringcvt::string_wide_from_utf8(site.label.c_str()));
                combo.SetItemData(row, static_cast<DWORD_PTR>(visible_sites.size()));
                if (site.id == preferred) selected = row;
                visible_sites.push_back(index);
            }
        }
        combo.SetCurSel(combo.GetCount() > 0 ? selected : -1);
        const auto site = SelectedSite();
        GetDlgItem(IDOK).EnableWindow(site ? TRUE : FALSE);
        uSetDlgItemText(*this, IDC_LX_STATUS, !site ? "" :
            site->rankings || site->categories ? "确认后打开当前站点的榜单；可继续按回车浏览集合。" :
            "确认后显示汽水公开目录里的随机音乐结果。");
        if (!site)
            MessageBoxW(L"当前音源没有可随便看看的站点，请选择其他音源。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
    }

    LRESULT OnSourceChanged(WORD, WORD, HWND, BOOL&) { FillSites(); return 0; }
    LRESULT OnSiteChanged(WORD, WORD, HWND, BOOL&) { return 0; }

    LRESULT OnOK(WORD, WORD, HWND, BOOL&) {
        const auto source = SelectedSource();
        const auto site = SelectedSite();
        if (!source || !site || !site->searchable || !site->discover) return 0;
        options.source = source->id;
        options.site = site->id;
        options.keyword.clear();
        options.type = "discover";
        options.quality = "flac";
        g_search_site_id = options.site.c_str();
        EndDialog(IDOK);
        return 0;
    }

    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
};

class page_dialog : public CDialogImpl<page_dialog> {
public:
    enum { IDD = IDD_LX_PAGE };
    SearchPage state;
    int page = 1;
    BEGIN_MSG_MAP(page_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()
    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        uSetDlgItemText(*this, IDC_LX_STATUS, search_page_label(state).c_str());
        SetDlgItemInt(IDC_LX_PAGE_NUMBER, state.page, FALSE);
        CEdit(GetDlgItem(IDC_LX_PAGE_NUMBER)).LimitText(9);
        CEdit(GetDlgItem(IDC_LX_PAGE_NUMBER)).SetSel(0, -1);
        GetDlgItem(IDC_LX_PAGE_NUMBER).SetFocus();
        return FALSE;
    }
    LRESULT OnOK(WORD, WORD, HWND, BOOL&) {
        if (!parse_search_page_number(dialog_text(*this, IDC_LX_PAGE_NUMBER), state.total_pages, page)) {
            const auto message = state.total_pages > 0 ? "请输入 1 到 " + std::to_string(state.total_pages) + " 之间的页码。" : "请输入有效的正整数页码。";
            MessageBoxW(pfc::stringcvt::string_wide_from_utf8(message.c_str()), L"页码无效", MB_OK | MB_ICONINFORMATION);
            CEdit(GetDlgItem(IDC_LX_PAGE_NUMBER)).SetSel(0, -1);
            GetDlgItem(IDC_LX_PAGE_NUMBER).SetFocus();
            return 0;
        }
        EndDialog(IDOK); return 0;
    }
    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
};

class source_manager_dialog : public CDialogImpl<source_manager_dialog> {
public:
    source_manager_dialog() : source_list_(this, 1) {}
    enum { IDD = IDD_LX_SOURCES };
    std::vector<SourceOption> sources;
    std::string selected;
    SourceAction action = SourceAction::close;
    BEGIN_MSG_MAP(source_manager_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        MSG_WM_DESTROY(OnDestroy)
        COMMAND_HANDLER(IDC_LX_SOURCE_LIST, LBN_SELCHANGE, OnSelectionChanged)
        MESSAGE_HANDLER(WM_CONTEXTMENU, OnContextMenu)
        COMMAND_ID_HANDLER(IDC_LX_IMPORT_FILE, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_IMPORT_URL, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_EDIT_SOURCE, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_UPDATE_SOURCE, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_COPY_SOURCE_URL, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_REMOVE_SOURCE, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_SELECT_SOURCE, OnAction)
        COMMAND_ID_HANDLER(IDCANCEL, OnAction)
        ALT_MSG_MAP(1)
        MESSAGE_HANDLER(WM_KEYDOWN, OnListKeyDown)
        MESSAGE_HANDLER(WM_CONTEXTMENU, OnContextMenu)
    END_MSG_MAP()
    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        source_list_.SubclassWindow(GetDlgItem(IDC_LX_SOURCE_LIST));
        auto& list = source_list_;
        int current = 0;
        for (size_t i = 0; i < sources.size(); ++i) {
            const auto label = sources[i].name + (sources[i].active ? "（当前）" : "");
            list.AddString(pfc::stringcvt::string_wide_from_utf8(label.c_str()));
            if (sources[i].active) current = static_cast<int>(i);
        }
        list.SetCurSel(current);
        list.SetFocus();
        UpdateDetails();
        if (sources.empty())
            MessageBoxW(L"尚未导入音源，请使用“从文件导入”或“从网址导入”添加脚本。", L"管理音源", MB_OK | MB_ICONINFORMATION);
        return TRUE;
    }
    void OnDestroy() { if (source_list_.IsWindow()) source_list_.UnsubclassWindow(); }
    const SourceOption* SelectedSource() const {
        const int index = CListBox(GetDlgItem(IDC_LX_SOURCE_LIST)).GetCurSel();
        return index >= 0 && static_cast<size_t>(index) < sources.size() ? &sources[index] : nullptr;
    }
    void UpdateDetails() {
        const auto source = SelectedSource();
        uSetDlgItemText(*this, IDC_LX_SOURCE_DETAILS, source ? source->details.c_str() : "");
    }
    LRESULT OnSelectionChanged(WORD, WORD, HWND, BOOL&) { UpdateDetails(); return 0; }
    LRESULT OnContextMenu(UINT, WPARAM source, LPARAM position, BOOL&) {
        if (source && GetTickCount() - suppress_keyboard_context_ < 250) return 0;
        POINT point{ GET_X_LPARAM(position), GET_Y_LPARAM(position) };
        if (point.x == -1 && point.y == -1) {
            const auto selected = CListBox(GetDlgItem(IDC_LX_SOURCE_LIST)).GetCurSel();
            RECT item{};
            if (selected >= 0 && CListBox(GetDlgItem(IDC_LX_SOURCE_LIST)).GetItemRect(selected, &item)) {
                ClientToScreen(&item);
                point.x = item.left;
                point.y = item.bottom;
            }
        }
        POINT client = point;
        ScreenToClient(&client);
        const auto hit = static_cast<LRESULT>(SendDlgItemMessage(IDC_LX_SOURCE_LIST,
            LB_ITEMFROMPOINT, 0, MAKELPARAM(client.x, client.y)));
        if (HIWORD(hit) != 0) return 0;
        const int index = LOWORD(hit);
        if (index < 0 || static_cast<size_t>(index) >= sources.size()) return 0;
        CListBox(GetDlgItem(IDC_LX_SOURCE_LIST)).SetCurSel(index);
        UpdateDetails();
        return ShowContextMenu(point, index);
    }
    LRESULT OnListKeyDown(UINT, WPARAM key, LPARAM, BOOL& handled) {
        const bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        if (key != VK_APPS && key != VK_MULTIPLY && !(key == VK_F10 && shift)) {
            handled = FALSE;
            return 0;
        }
        const int index = source_list_.GetCurSel();
        if (index < 0 || static_cast<size_t>(index) >= sources.size()) return 0;
        RECT item{};
        source_list_.GetItemRect(index, &item);
        source_list_.ClientToScreen(&item);
        suppress_keyboard_context_ = GetTickCount();
        return ShowContextMenu(POINT{ item.left, item.bottom }, index);
    }
    LRESULT ShowContextMenu(POINT point, int index) {
        HMENU menu = CreatePopupMenu();
        if (!menu) return 0;
        const auto source = &sources[index];
        AppendMenuW(menu, MF_STRING | (source->active ? MF_GRAYED : 0), IDC_LX_SELECT_SOURCE, L"设为当前");
        AppendMenuW(menu, MF_STRING, IDC_LX_EDIT_SOURCE, L"编辑脚本…");
        AppendMenuW(menu, MF_STRING, IDC_LX_UPDATE_SOURCE, L"检查更新");
        AppendMenuW(menu, MF_STRING | (source->url_available ? 0 : MF_GRAYED), IDC_LX_COPY_SOURCE_URL, L"复制音源地址");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, IDC_LX_REMOVE_SOURCE, L"删除");
        SetForegroundWindow(*this);
        const auto command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
            point.x, point.y, 0, *this, nullptr);
        DestroyMenu(menu);
        PostMessage(WM_NULL);
        if (command) PerformAction(static_cast<WORD>(command));
        return 0;
    }
    LRESULT PerformAction(WORD id) {
        if (id == IDC_LX_COPY_SOURCE_URL) {
            const auto source = SelectedSource();
            if (!source || source->url.empty()) return 0;
            try {
                copy_text_to_clipboard(source->url);
                MessageBoxW(L"已复制音源地址", L"管理音源", MB_OK | MB_ICONINFORMATION);
            } catch (const std::exception& ex) {
                ::MessageBoxW(*this, pfc::stringcvt::string_wide_from_utf8(ex.what()),
                    L"复制音源地址", MB_OK | MB_ICONINFORMATION);
            }
            source_list_.SetFocus();
            return 0;
        }
        if (id == IDC_LX_IMPORT_FILE) action = SourceAction::import_file;
        else if (id == IDC_LX_IMPORT_URL) action = SourceAction::import_url;
        else if (id == IDC_LX_REMOVE_SOURCE || id == IDC_LX_SELECT_SOURCE || id == IDC_LX_EDIT_SOURCE ||
            id == IDC_LX_UPDATE_SOURCE) {
            const auto source = SelectedSource();
            if (!source) return 0;
            selected = source->id;
            action = id == IDC_LX_SELECT_SOURCE ? SourceAction::select :
                id == IDC_LX_REMOVE_SOURCE ? SourceAction::remove :
                id == IDC_LX_EDIT_SOURCE ? SourceAction::edit :
                SourceAction::update;
        }
        EndDialog(IDOK);
        return 0;
    }
    LRESULT OnAction(WORD, WORD id, HWND, BOOL&) {
        return PerformAction(id);
    }
private:
    CContainedWindowT<CListBox> source_list_;
    DWORD suppress_keyboard_context_ = 0;
};

class source_editor_dialog : public CDialogImpl<source_editor_dialog> {
public:
    enum { IDD = IDD_LX_SOURCE_EDITOR };
    std::string title, script, revision;
    BEGIN_MSG_MAP(source_editor_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()
    BOOL OnInitDialog(CWindow, LPARAM) {
        SetWindowText(pfc::stringcvt::string_wide_from_utf8(title.c_str()));
        auto editor = GetDlgItem(IDC_LX_EDITOR_TEXT);
        editor.SendMessage(EM_SETLIMITTEXT, 2 * 1024 * 1024, 0);
        editor.SetWindowText(pfc::stringcvt::string_wide_from_utf8(script.c_str()));
        editor.SetFocus();
        return FALSE;
    }
    LRESULT OnOK(WORD, WORD, HWND, BOOL&) {
        pfc::string8 value;
        uGetDlgItemText(*this, IDC_LX_EDITOR_TEXT, value);
        script = value.c_str();
        if (script.empty()) {
            MessageBoxW(L"音源脚本不能为空。", L"编辑 LX 音源", MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        EndDialog(IDOK);
        return 0;
    }
    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
};

class download_done_dialog : public CDialogImpl<download_done_dialog> {
public:
    enum { IDD = IDD_LX_DOWNLOAD_DONE };
    std::string summary, folder;
    BEGIN_MSG_MAP(download_done_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_ID_HANDLER(IDC_LX_OPEN_DOWNLOAD_DIR, OnOpen)
        COMMAND_ID_HANDLER(IDCANCEL, OnClose)
    END_MSG_MAP()
    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        uSetDlgItemText(*this, IDC_LX_DOWNLOAD_DONE_TEXT, summary.c_str());
        GetDlgItem(IDC_LX_OPEN_DOWNLOAD_DIR).EnableWindow(folder.empty() ? FALSE : TRUE);
        return TRUE;
    }
    LRESULT OnOpen(WORD, WORD, HWND, BOOL&) {
        if (!folder.empty()) {
            const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(*this, L"open",
                pfc::stringcvt::string_wide_from_utf8(folder.c_str()), nullptr, nullptr, SW_SHOWNORMAL));
            if (result <= 32) MessageBoxW(L"无法打开下载文件夹。", L"LX 批量下载", MB_OK | MB_ICONINFORMATION);
        }
        return 0;
    }
    LRESULT OnClose(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
};

class update_dialog : public CDialogImpl<update_dialog> {
public:
    enum { IDD = IDD_LX_UPDATE };
    std::string version, changelog;
    BEGIN_MSG_MAP(update_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()
    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        uSetDlgItemText(*this, IDC_LX_UPDATE_VERSION, version.c_str());
        uSetDlgItemText(*this, IDC_LX_UPDATE_NOTES,
            changelog.empty() ? "本次更新没有提供详细说明。" : changelog.c_str());
        GetDlgItem(IDOK).SetFocus();
        return FALSE;
    }
    LRESULT OnOK(WORD, WORD, HWND, BOOL&) { EndDialog(IDOK); return 0; }
    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
};

class url_dialog : public CDialogImpl<url_dialog> {
public:
    enum { IDD = IDD_LX_URL };
    std::string value;

    BEGIN_MSG_MAP(url_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()

    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        GetDlgItem(IDC_LX_URL).SetFocus();
        return FALSE;
    }
    LRESULT OnOK(WORD, WORD, HWND, BOOL&) {
        value = dialog_text(*this, IDC_LX_URL);
        if (value.rfind("http://", 0) != 0 && value.rfind("https://", 0) != 0) {
            MessageBoxW(L"请输入完整的 HTTP 或 HTTPS 音源脚本网址。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        EndDialog(IDOK);
        return 0;
    }
    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
};

class lyrics_dialog : public CDialogImpl<lyrics_dialog> {
public:
    enum { IDD = IDD_LX_LYRICS };
    lyrics_dialog(std::string caption, std::string lyrics) : m_caption(std::move(caption)), m_lyrics(std::move(lyrics)) {}

    BEGIN_MSG_MAP(lyrics_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        MSG_WM_SIZE(OnSize)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()

    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        uSetWindowText(*this, m_caption.c_str());
        uSetDlgItemText(*this, IDC_LX_LYRICS, m_lyrics.c_str());
        GetDlgItem(IDC_LX_LYRICS).SetFocus();
        return FALSE;
    }
    void OnSize(UINT, CSize size) {
        if (!IsWindow()) return;
        const int margin = 8;
        const int buttonWidth = 78;
        const int buttonHeight = 22;
        const int width = static_cast<int>(size.cx);
        const int height = static_cast<int>(size.cy);
        if (auto edit = GetDlgItem(IDC_LX_LYRICS); edit.IsWindow())
            edit.SetWindowPos(nullptr, margin, margin, std::max(20, width - margin * 2),
                std::max(20, height - buttonHeight - margin * 3), SWP_NOZORDER);
        if (auto button = GetDlgItem(IDCANCEL); button.IsWindow())
            button.SetWindowPos(nullptr, std::max(margin, width - buttonWidth - margin),
                std::max(margin, height - buttonHeight - margin), buttonWidth, buttonHeight, SWP_NOZORDER);
    }
    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
private:
    std::string m_caption, m_lyrics;
};

class download_dialog : public CDialogImpl<download_dialog> {
public:
    enum { IDD = IDD_LX_DOWNLOAD };
    std::vector<std::string> payloads;
    std::string folder;
    std::string quality = "320k";
    std::string naming_template;
    DownloadOptions options;

    BEGIN_MSG_MAP(download_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        MSG_WM_DESTROY(OnDestroy)
        MSG_WM_TIMER(OnTimer)
        COMMAND_HANDLER(IDC_LX_BROWSE, BN_CLICKED, OnBrowse)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_NAMING, CBN_EDITCHANGE, OnNamingChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_NAMING, CBN_SELCHANGE, OnNamingChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_QUALITY, CBN_SELCHANGE, OnNamingChanged)
        COMMAND_HANDLER(IDC_LX_SELECT_ALL, BN_CLICKED, OnSelectAll)
        COMMAND_HANDLER(IDC_LX_SELECT_NONE, BN_CLICKED, OnSelectNone)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_ALL_PAGES, BN_CLICKED, OnScopeChanged)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()

    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        if (folder.empty()) {
            wchar_t path[MAX_PATH]{};
            if (SUCCEEDED(SHGetFolderPathW(*this, CSIDL_MYMUSIC | CSIDL_FLAG_CREATE, nullptr, SHGFP_TYPE_CURRENT, path))) {
                pfc::stringcvt::string_utf8_from_wide converted(path);
                folder = converted.get_ptr();
            }
        }
        uSetDlgItemText(*this, IDC_LX_FOLDER, folder.c_str());
        auto combo = CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_QUALITY));
        combo.AddString(L"128K"); combo.AddString(L"320K"); combo.AddString(L"FLAC"); combo.AddString(L"Hi-Res / 24bit");
        int quality_index = 1;
        if (quality == "128k") quality_index = 0;
        else if (quality == "flac") quality_index = 2;
        else if (quality == "flac24bit" || quality == "hires") quality_index = 3;
        combo.SetCurSel(quality_index);

        fill_download_naming(GetDlgItem(IDC_LX_DOWNLOAD_NAMING), naming_template);
        auto workers = CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_WORKERS));
        for (int i = 1; i <= 16; ++i) workers.AddString(std::to_wstring(i).c_str());
        workers.SetCurSel(std::clamp(options.workers, 1, 16) - 1);
        preview->window = m_hWnd;
        SchedulePreview();
        GetDlgItem(IDC_LX_DOWNLOAD_ALL_PAGES).EnableWindow(options.can_all_pages ? TRUE : FALSE);
        CheckDlgButton(IDC_LX_DOWNLOAD_ALL_PAGES, options.all_pages ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(IDC_LX_DOWNLOAD_SKIP_FAILURES, options.skip_failures ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(IDC_LX_DOWNLOAD_CLEANUP, options.cleanup_duplicates ? BST_CHECKED : BST_UNCHECKED);
        UpdateScope();

        INITCOMMONCONTROLSEX common_controls{ sizeof(common_controls), ICC_LISTVIEW_CLASSES };
        InitCommonControlsEx(&common_controls);
        const auto list = GetDlgItem(IDC_LX_DOWNLOAD_ITEMS).m_hWnd;
        ListView_SetExtendedListViewStyleEx(list, LVS_EX_CHECKBOXES | LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER,
            LVS_EX_CHECKBOXES | LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER);
        LVCOLUMNW column{};
        column.mask = LVCF_TEXT | LVCF_WIDTH;
        column.cx = 385;
        column.pszText = const_cast<wchar_t*>(L"下载项目");
        ListView_InsertColumn(list, 0, &column);
        for (size_t index = 0; index < payloads.size(); ++index) {
            std::string label;
            try {
                const auto payload = decode_payload(payloads[index]);
                const bool ranking = payload.get(pf_kind) == "playlist" &&
                    payload.get(pf_collection_kind) == "Ranking";
                if (ranking) label = payload.get(pf_title) + "（整个榜单）";
                else {
                    label = payload.get(pf_artist);
                    if (!label.empty()) label += " - ";
                    label += payload.get(pf_title);
                    if (payload.get(pf_kind) == "album") label += "（整张专辑，含封面）";
                }
            } catch (...) {
                label = "在线歌曲 " + std::to_string(index + 1);
            }
            pfc::stringcvt::string_wide_from_utf8 wide_label(label.c_str());
            LVITEMW item{};
            item.mask = LVIF_TEXT;
            item.iItem = static_cast<int>(index);
            item.pszText = const_cast<wchar_t*>(wide_label.get_ptr());
            ListView_InsertItem(list, &item);
            ListView_SetCheckState(list, static_cast<int>(index), TRUE);
        }
        if (!g_download_overrides) MakeCompact();
        else GetDlgItem(IDC_LX_DOWNLOAD_SUMMARY).ShowWindow(SW_HIDE);
        return TRUE;
    }

    LRESULT OnSelectAll(WORD, WORD, HWND, BOOL&) { SetAllChecks(true); return 0; }
    LRESULT OnSelectNone(WORD, WORD, HWND, BOOL&) { SetAllChecks(false); return 0; }
    LRESULT OnScopeChanged(WORD, WORD, HWND, BOOL&) { UpdateScope(); return 0; }

    void OnDestroy() {
        KillTimer(preview_timer);
        preview->window = nullptr;
        ++preview->generation;
    }

    LRESULT OnNamingChanged(WORD, WORD, HWND, BOOL&) { SchedulePreview(); return 0; }

    void OnTimer(UINT_PTR id) {
        if (id != preview_timer) return;
        KillTimer(preview_timer);
        const auto state = preview;
        const auto generation = state->generation;
        const auto pattern = dialog_text(*this, IDC_LX_DOWNLOAD_NAMING);
        if (pattern.empty()) {
            state->error = "请输入文件命名格式。";
            uSetDlgItemText(*this, IDC_LX_DOWNLOAD_PREVIEW, "");
            GetDlgItem(IDOK).EnableWindow(TRUE);
            return;
        }
        const auto selected_quality = SelectedQuality();
        fb2k::splitTask([state, generation, pattern, selected_quality] {
            std::string message;
            bool valid = false;
            try {
                const auto result = run_bridge({ "download-name-preview", base64url_encode_text(pattern), selected_quality }, fb2k::noAbort);
                message = bridge_message(result, "NAME");
                valid = !message.empty();
                if (!valid) message = "未能生成文件名示例";
            } catch (const std::exception& ex) { message = ex.what(); }
            catch (...) { message = "未能生成文件名示例"; }
            fb2k::inMainThread([state, generation, message, valid] {
                if (!state->window || state->generation != generation) return;
                state->valid = valid;
                state->error = valid ? "" : message;
                uSetDlgItemText(state->window, IDC_LX_DOWNLOAD_PREVIEW, valid ? message.c_str() : "");
                ::EnableWindow(::GetDlgItem(state->window, IDOK), TRUE);
            });
        });
    }

    LRESULT OnBrowse(WORD, WORD, HWND, BOOL&) {
        BROWSEINFOW info{};
        info.hwndOwner = *this;
        info.lpszTitle = L"选择在线歌曲下载目录";
        info.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
        PIDLIST_ABSOLUTE id = SHBrowseForFolderW(&info);
        if (id) {
            wchar_t path[MAX_PATH]{};
            if (SHGetPathFromIDListW(id, path)) {
                pfc::stringcvt::string_utf8_from_wide converted(path);
                folder = converted.get_ptr();
                uSetDlgItemText(*this, IDC_LX_FOLDER, folder.c_str());
            }
            CoTaskMemFree(id);
        }
        return 0;
    }

    LRESULT OnOK(WORD, WORD, HWND, BOOL&) {
        if (!preview->valid) {
            const auto message = preview->error.empty() ? "正在生成文件名示例，请稍候。" : preview->error.c_str();
            MessageBoxW(pfc::stringcvt::string_wide_from_utf8(message), L"文件命名格式无效", MB_OK | MB_ICONINFORMATION);
            GetDlgItem(IDC_LX_DOWNLOAD_NAMING).SetFocus();
            return 0;
        }
        folder = dialog_text(*this, IDC_LX_FOLDER);
        if (folder.empty()) {
            MessageBoxW(L"请选择下载目录。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        quality = SelectedQuality();
        naming_template = dialog_text(*this, IDC_LX_DOWNLOAD_NAMING);
        options.all_pages = options.can_all_pages && IsDlgButtonChecked(IDC_LX_DOWNLOAD_ALL_PAGES) == BST_CHECKED;
        options.skip_failures = IsDlgButtonChecked(IDC_LX_DOWNLOAD_SKIP_FAILURES) == BST_CHECKED;
        options.cleanup_duplicates = IsDlgButtonChecked(IDC_LX_DOWNLOAD_CLEANUP) == BST_CHECKED;
        options.workers = std::clamp(CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_WORKERS)).GetCurSel() + 1, 1, 16);

        std::vector<std::string> checked;
        const auto list = GetDlgItem(IDC_LX_DOWNLOAD_ITEMS).m_hWnd;
        for (size_t index = 0; index < payloads.size(); ++index) {
            if (ListView_GetCheckState(list, static_cast<int>(index))) checked.push_back(payloads[index]);
        }
        if (checked.empty() && !options.all_pages) {
            MessageBoxW(L"请至少勾选一个下载项目。", L"LX 批量下载", MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        payloads = std::move(checked);
        EndDialog(IDOK);
        return 0;
    }
    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
private:
    void MoveControl(int id, int x, int y, int width, int height) {
        RECT rect{ x, y, x + width, y + height };
        MapDialogRect(&rect);
        GetDlgItem(id).SetWindowPos(nullptr, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, SWP_NOZORDER);
    }

    void MakeCompact() {
        KillTimer(preview_timer);
        ++preview->generation;
        preview->valid = true;
        GetDlgItem(IDOK).EnableWindow(TRUE);
        for (const auto id : { IDC_LX_FOLDER, IDC_LX_BROWSE, IDC_LX_DOWNLOAD_QUALITY, IDC_LX_DOWNLOAD_WORKERS,
            IDC_LX_DOWNLOAD_NAMING, IDC_LX_DOWNLOAD_PREVIEW, IDC_LX_DOWNLOAD_SKIP_FAILURES, IDC_LX_DOWNLOAD_CLEANUP,
            IDC_LX_FOLDER_LABEL, IDC_LX_QUALITY_LABEL, IDC_LX_WORKERS_LABEL, IDC_LX_NAMING_LABEL,
            IDC_LX_NAMING_HINT, IDC_LX_PREVIEW_LABEL, IDC_LX_CLEANUP_HINT }) GetDlgItem(id).ShowWindow(SW_HIDE);
        const auto summary = "保存到：" + folder + "\n" + quality + " · 同时 " + std::to_string(options.workers) +
            " 个任务；默认参数可在“首选项 → 工具 → LX 在线音乐”修改。";
        uSetDlgItemText(*this, IDC_LX_DOWNLOAD_SUMMARY, summary.c_str());
        GetDlgItem(IDC_LX_DOWNLOAD_SUMMARY).ShowWindow(SW_SHOW);
        MoveControl(IDC_LX_DOWNLOAD_ALL_PAGES, 10, 10, 410, 12);
        MoveControl(IDC_LX_DOWNLOAD_SUMMARY, 10, 30, 410, 25);
        MoveControl(IDC_LX_DOWNLOAD_ITEMS_LABEL, 10, 64, 265, 10);
        MoveControl(IDC_LX_SELECT_ALL, 302, 59, 55, 16);
        MoveControl(IDC_LX_SELECT_NONE, 365, 59, 55, 16);
        MoveControl(IDC_LX_DOWNLOAD_ITEMS, 10, 80, 410, 140);
        MoveControl(IDOK, 295, 230, 62, 16);
        MoveControl(IDCANCEL, 365, 230, 55, 16);
        RECT wanted{ 0, 0, 430, 256 }, client{}, window{};
        MapDialogRect(&wanted); GetClientRect(&client); GetWindowRect(&window);
        SetWindowPos(nullptr, 0, 0, window.right - window.left,
            wanted.bottom + (window.bottom - window.top) - client.bottom, SWP_NOMOVE | SWP_NOZORDER);
        CenterWindow(GetParent());
    }

    void UpdateScope() {
        const bool all = options.can_all_pages && IsDlgButtonChecked(IDC_LX_DOWNLOAD_ALL_PAGES) == BST_CHECKED;
        for (const auto control : { IDC_LX_DOWNLOAD_ITEMS, IDC_LX_SELECT_ALL, IDC_LX_SELECT_NONE })
            GetDlgItem(control).EnableWindow(all ? FALSE : TRUE);
        uSetDlgItemText(*this, IDC_LX_DOWNLOAD_ITEMS_LABEL, all ? "将从第 1 页逐页下载全部结果。" : "下载项目（专辑和榜单包含全部歌曲）：");
    }

    static constexpr UINT_PTR preview_timer = 21;
    struct preview_state { HWND window = nullptr; size_t generation = 0; bool valid = false; std::string error; };
    std::shared_ptr<preview_state> preview = std::make_shared<preview_state>();

    std::string SelectedQuality() {
        static const char* values[] = { "128k", "320k", "flac", "flac24bit" };
        return values[std::clamp(CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_QUALITY)).GetCurSel(), 0, 3)];
    }

    void SchedulePreview() {
        if (!preview->window) return;
        ++preview->generation;
        preview->valid = false;
        preview->error.clear();
        GetDlgItem(IDOK).EnableWindow(FALSE);
        uSetDlgItemText(*this, IDC_LX_DOWNLOAD_PREVIEW, "正在生成示例…");
        KillTimer(preview_timer);
        SetTimer(preview_timer, 250);
    }

    void SetAllChecks(bool checked) {
        const auto list = GetDlgItem(IDC_LX_DOWNLOAD_ITEMS).m_hWnd;
        for (int index = 0, count = ListView_GetItemCount(list); index < count; ++index)
            ListView_SetCheckState(list, index, checked ? TRUE : FALSE);
    }
};

class preferences_dialog : public CDialogImpl<preferences_dialog>, public preferences_page_instance {
public:
    enum { IDD = IDD_LX_PREFERENCES };
    explicit preferences_dialog(preferences_page_callback::ptr callback) : callback_(callback) {}
    t_uint32 get_state() override {
        return preferences_state::resettable | preferences_state::dark_mode_supported |
            (changed_ ? preferences_state::changed : 0);
    }
    void apply() override {
        const auto pattern = dialog_text(*this, IDC_LX_DOWNLOAD_NAMING);
        try {
            const auto check = run_bridge_noabort({ "download-name-preview", base64url_encode_text(pattern), "flac" });
            if (bridge_message(check, "NAME").empty()) throw std::runtime_error("文件命名格式无效");
        } catch (const std::exception& ex) {
            MessageBoxW(pfc::stringcvt::string_wide_from_utf8(ex.what()), L"文件命名格式无效", MB_OK | MB_ICONINFORMATION);
            GetDlgItem(IDC_LX_DOWNLOAD_NAMING).SetFocus();
            return;
        }
        g_download_folder = dialog_text(*this, IDC_LX_FOLDER).c_str();
        static const char* qualities[] = { "128k", "320k", "flac", "flac24bit" };
        g_download_quality = qualities[std::clamp(CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_QUALITY)).GetCurSel(), 0, 3)];
        g_download_naming = pattern.c_str();
        g_download_workers = std::clamp(CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_WORKERS)).GetCurSel() + 1, 1, 16);
        g_download_skip_failures = IsDlgButtonChecked(IDC_LX_DOWNLOAD_SKIP_FAILURES) == BST_CHECKED;
        g_download_cleanup = IsDlgButtonChecked(IDC_LX_DOWNLOAD_CLEANUP) == BST_CHECKED;
        g_download_overrides = IsDlgButtonChecked(IDC_LX_DOWNLOAD_OVERRIDES) == BST_CHECKED;
        g_auto_updates = IsDlgButtonChecked(IDC_LX_AUTO_UPDATES) == BST_CHECKED;
        g_apply_search_naming = IsDlgButtonChecked(IDC_LX_APPLY_SEARCH_NAMING) == BST_CHECKED;
        refresh_playlist_payload_info();
        changed_ = false;
        callback_->on_state_changed();
        start_update_checks();
    }
    void reset() override {
        Load(true);
        changed_ = true;
        callback_->on_state_changed();
    }
    BEGIN_MSG_MAP(preferences_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_HANDLER(IDC_LX_FOLDER, EN_CHANGE, OnChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_NAMING, CBN_EDITCHANGE, OnChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_NAMING, CBN_SELCHANGE, OnChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_QUALITY, CBN_SELCHANGE, OnChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_WORKERS, CBN_SELCHANGE, OnChanged)
        COMMAND_HANDLER(IDC_LX_AUTO_UPDATES, BN_CLICKED, OnChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_OVERRIDES, BN_CLICKED, OnChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_SKIP_FAILURES, BN_CLICKED, OnChanged)
        COMMAND_HANDLER(IDC_LX_DOWNLOAD_CLEANUP, BN_CLICKED, OnChanged)
        COMMAND_HANDLER(IDC_LX_APPLY_SEARCH_NAMING, BN_CLICKED, OnChanged)
        COMMAND_HANDLER(IDC_LX_BROWSE, BN_CLICKED, OnBrowse)
        COMMAND_HANDLER(IDC_LX_CHECK_NOW, BN_CLICKED, OnCheck)
    END_MSG_MAP()
private:
    preferences_page_callback::ptr callback_;
    fb2k::CCoreDarkModeHooks dark_;
    bool changed_ = false, loading_ = true;
    BOOL OnInitDialog(CWindow, LPARAM) {
        dark_.AddDialogWithControls(*this);
        auto quality = CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_QUALITY));
        for (const auto label : { L"128K", L"320K", L"FLAC", L"Hi-Res / 24bit" }) quality.AddString(label);
        auto workers = CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_WORKERS));
        for (int i = 1; i <= 16; ++i) workers.AddString(std::to_wstring(i).c_str());
        fill_download_naming(GetDlgItem(IDC_LX_DOWNLOAD_NAMING), g_download_naming.get().c_str());
        Load(false);
        return FALSE;
    }
    void Load(bool defaults) {
        loading_ = true;
        const std::string folder = defaults || g_download_folder.get().is_empty() ? default_download_folder() : g_download_folder.get().c_str();
        uSetDlgItemText(*this, IDC_LX_FOLDER, folder.c_str());
        uSetDlgItemText(*this, IDC_LX_DOWNLOAD_NAMING, defaults ? "{序号} - {曲名} - {艺术家}" : g_download_naming.get().c_str());
        const std::string quality = defaults ? "flac" : g_download_quality.get().c_str();
        CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_QUALITY)).SetCurSel(quality == "128k" ? 0 : quality == "320k" ? 1 : quality == "flac" ? 2 : 3);
        CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_WORKERS)).SetCurSel((defaults ? 3 : std::clamp(static_cast<int>(g_download_workers), 1, 16)) - 1);
        CheckDlgButton(IDC_LX_AUTO_UPDATES, defaults || g_auto_updates ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(IDC_LX_DOWNLOAD_OVERRIDES, defaults || g_download_overrides ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(IDC_LX_DOWNLOAD_SKIP_FAILURES, defaults || g_download_skip_failures ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(IDC_LX_DOWNLOAD_CLEANUP, !defaults && g_download_cleanup ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(IDC_LX_APPLY_SEARCH_NAMING, !defaults && g_apply_search_naming ? BST_CHECKED : BST_UNCHECKED);
        loading_ = false;
    }
    LRESULT OnChanged(WORD, WORD, HWND, BOOL&) {
        if (!loading_) { changed_ = true; callback_->on_state_changed(); }
        return 0;
    }
    LRESULT OnBrowse(WORD, WORD, HWND, BOOL&) {
        BROWSEINFOW info{};
        info.hwndOwner = *this;
        info.lpszTitle = L"选择默认下载目录";
        info.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
        if (auto selected = SHBrowseForFolderW(&info)) {
            wchar_t path[MAX_PATH]{};
            if (SHGetPathFromIDListW(selected, path)) SetDlgItemText(IDC_LX_FOLDER, path);
            CoTaskMemFree(selected);
        }
        return 0;
    }
    LRESULT OnCheck(WORD, WORD, HWND, BOOL&) { command_check_update(); return 0; }
};

class preferences_page_lxmusic : public preferences_page_impl<preferences_dialog> {
public:
    const char* get_name() override { return "LX 在线音乐"; }
    GUID get_guid() override { return guid_preferences; }
    GUID get_parent_guid() override { return preferences_page::guid_tools; }
};
static preferences_page_factory_t<preferences_page_lxmusic> g_preferences_page;

} // namespace

std::string download_naming_template() { return g_download_naming.get().c_str(); }
bool apply_naming_to_search() { return g_apply_search_naming; }
bool automatic_updates_enabled() { return g_auto_updates; }
void show_component_preferences() { ui_control::get()->show_preferences(guid_preferences); }

bool show_search_dialog(SearchOptions& options, const std::vector<SourceOption>& sources) {
    search_dialog dialog;
    dialog.sources = sources;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    options = dialog.options;
    return true;
}

bool show_browse_dialog(SearchOptions& options, const std::vector<SourceOption>& sources) {
    browse_dialog dialog;
    dialog.sources = sources;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    options = dialog.options;
    return true;
}

bool show_page_dialog(const SearchPage& state, int& page, HWND owner) {
    page_dialog dialog;
    dialog.state = state;
    if (dialog.DoModal(owner && ::IsWindow(owner) ? owner : core_api::get_main_window()) != IDOK) return false;
    page = dialog.page;
    return true;
}

void remember_search(const SearchOptions& options) {
    g_search_history = update_search_history(g_search_history.get().c_str(), options).c_str();
}

SourceAction show_source_manager(const std::vector<SourceOption>& sources, std::string& selected) {
    source_manager_dialog dialog;
    dialog.sources = sources;
    dialog.DoModal(core_api::get_main_window());
    selected = dialog.selected;
    return dialog.action;
}

bool show_source_editor(const std::string& title, std::string& script, std::string& revision) {
    source_editor_dialog dialog;
    dialog.title = title;
    dialog.script = script;
    dialog.revision = revision;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    script = dialog.script;
    revision = dialog.revision;
    return true;
}

bool show_url_dialog(std::string& url) {
    url_dialog dialog;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    url = dialog.value;
    return true;
}

bool show_update_dialog(const std::string& version, const std::string& changelog) {
    update_dialog dialog;
    dialog.version = version;
    dialog.changelog = changelog;
    return dialog.DoModal(core_api::get_main_window()) == IDOK;
}

void show_lyrics_dialog(const std::string& caption, const std::string& lyrics) {
    lyrics_dialog dialog(caption, lyrics);
    dialog.DoModal(core_api::get_main_window());
}

void show_download_complete(const std::string& summary, const std::string& folder, HWND owner) {
    download_done_dialog dialog;
    dialog.summary = summary;
    dialog.folder = folder;
    dialog.DoModal(owner && ::IsWindow(owner) ? owner : core_api::get_main_window());
}

bool show_download_dialog(std::vector<std::string>& payloads, std::string& folder, std::string& quality,
    std::string& naming_template, DownloadOptions& options, HWND owner) {
    download_dialog dialog;
    dialog.payloads = payloads;
    dialog.folder = folder.empty() ? std::string(g_download_folder.get().c_str()) : folder;
    dialog.quality = quality.empty() ? std::string(g_download_quality.get().c_str()) : quality;
    dialog.naming_template = naming_template.empty() ? std::string(g_download_naming.get().c_str()) : naming_template;
    dialog.options = options;
    dialog.options.skip_failures = g_download_skip_failures;
    dialog.options.cleanup_duplicates = g_download_cleanup;
    dialog.options.workers = std::clamp(static_cast<int>(g_download_workers), 1, 16);
    if (dialog.DoModal(owner && ::IsWindow(owner) ? owner : core_api::get_main_window()) != IDOK) return false;
    folder = dialog.folder;
    quality = dialog.quality;
    naming_template = dialog.naming_template;
    options = dialog.options;
    payloads = std::move(dialog.payloads);
    return true;
}

} // namespace lxmusic
