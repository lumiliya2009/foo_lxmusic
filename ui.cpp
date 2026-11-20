#include "stdafx.h"

namespace lxmusic {

namespace {

cfg_int g_search_site({ 0x628a3da1, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, 0);
cfg_int g_search_type({ 0x628a3da2, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, 0);
cfg_string g_search_keyword({ 0x628a3da3, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_download_folder({ 0x628a3da4, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_download_quality({ 0x628a3da5, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "flac");
cfg_string g_search_site_id({ 0x628a3da6, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_search_history({ 0x628a3da7, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");

std::string dialog_text(HWND dialog, UINT id) {
    pfc::string8 value;
    uGetDlgItemText(dialog, id, value);
    return value.c_str();
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
            label = entry.keyword + "（" + label + " / " +
                (entry.type == "album" ? "专辑" : entry.type == "artist" ? "歌手" : entry.type == "playlist" ? "歌单" : "单曲") + "）";
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
        type.AddString(L"单曲");
        if (site && site->collections) {
            type.AddString(L"专辑"); type.AddString(L"歌手"); type.AddString(L"歌单");
        }
        type.SetCurSel(site && site->collections ? std::clamp(static_cast<int>(g_search_type), 0, 3) : 0);
        GetDlgItem(IDOK).EnableWindow(site != nullptr && site->searchable);
        uSetDlgItemText(*this, IDC_LX_STATUS, !site ? "当前音源没有可搜索的站点。" :
            !site->searchable ? "音源支持此站点的播放解析，但未提供搜索接口。" :
            site->collections ? "结果中按回车或双击：打开集合，播放歌曲。" : "此站点支持单曲搜索，结果中按回车播放。" );
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
        const int type = entry.type == "album" ? 1 : entry.type == "artist" ? 2 : entry.type == "playlist" ? 3 : 0;
        auto types = CComboBox(GetDlgItem(IDC_LX_TYPE));
        types.SetCurSel(type < types.GetCount() ? type : 0);
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
        if (options.keyword.empty()) {
            MessageBoxW(L"请输入搜索关键词。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
            GetDlgItem(IDC_LX_KEYWORD).SetFocus();
            return 0;
        }
        static const char* types[] = { "track", "album", "artist", "playlist" };
        const auto source = SelectedSource();
        const auto site = SelectedSite();
        if (!source || !site || !site->searchable) return 0;
        const int type = std::clamp(CComboBox(GetDlgItem(IDC_LX_TYPE)).GetCurSel(), 0, 3);
        options.source = source->id;
        options.site = site->id;
        options.type = types[type];
        options.quality = "flac";
        g_search_site_id = options.site.c_str();
        g_search_type = type;
        g_search_keyword = options.keyword.c_str();
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
    enum { IDD = IDD_LX_SOURCES };
    std::vector<SourceOption> sources;
    std::string selected;
    SourceAction action = SourceAction::close;
    BEGIN_MSG_MAP(source_manager_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_ID_HANDLER(IDC_LX_IMPORT_FILE, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_IMPORT_URL, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_REMOVE_SOURCE, OnAction)
        COMMAND_ID_HANDLER(IDC_LX_SELECT_SOURCE, OnAction)
        COMMAND_ID_HANDLER(IDCANCEL, OnAction)
    END_MSG_MAP()
    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        auto list = CListBox(GetDlgItem(IDC_LX_SOURCE_LIST));
        int current = 0;
        for (size_t i = 0; i < sources.size(); ++i) {
            const auto label = sources[i].name + (sources[i].active ? "（当前）" : "");
            list.AddString(pfc::stringcvt::string_wide_from_utf8(label.c_str()));
            if (sources[i].active) current = static_cast<int>(i);
        }
        list.SetCurSel(current);
        GetDlgItem(IDC_LX_REMOVE_SOURCE).EnableWindow(!sources.empty());
        GetDlgItem(IDC_LX_SELECT_SOURCE).EnableWindow(!sources.empty());
        return TRUE;
    }
    LRESULT OnAction(WORD, WORD id, HWND, BOOL&) {
        if (id == IDC_LX_IMPORT_FILE) action = SourceAction::import_file;
        else if (id == IDC_LX_IMPORT_URL) action = SourceAction::import_url;
        else if (id == IDC_LX_REMOVE_SOURCE || id == IDC_LX_SELECT_SOURCE) {
            const int i = CListBox(GetDlgItem(IDC_LX_SOURCE_LIST)).GetCurSel();
            if (i < 0 || static_cast<size_t>(i) >= sources.size()) return 0;
            selected = sources[i].id;
            action = id == IDC_LX_SELECT_SOURCE ? SourceAction::select : SourceAction::remove;
        }
        EndDialog(IDOK); return 0;
    }
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

    BEGIN_MSG_MAP(download_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_HANDLER(IDC_LX_BROWSE, BN_CLICKED, OnBrowse)
        COMMAND_HANDLER(IDC_LX_SELECT_ALL, BN_CLICKED, OnSelectAll)
        COMMAND_HANDLER(IDC_LX_SELECT_NONE, BN_CLICKED, OnSelectNone)
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
                label = payload.get(pf_artist);
                if (!label.empty()) label += " - ";
                label += payload.get(pf_title);
                if (payload.get(pf_kind) == "album") label += "（整张专辑，含封面）";
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
        return TRUE;
    }

    LRESULT OnSelectAll(WORD, WORD, HWND, BOOL&) { SetAllChecks(true); return 0; }
    LRESULT OnSelectNone(WORD, WORD, HWND, BOOL&) { SetAllChecks(false); return 0; }

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
        folder = dialog_text(*this, IDC_LX_FOLDER);
        if (folder.empty()) {
            MessageBoxW(L"请选择下载目录。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        static const char* values[] = { "128k", "320k", "flac", "flac24bit" };
        quality = values[std::clamp(CComboBox(GetDlgItem(IDC_LX_DOWNLOAD_QUALITY)).GetCurSel(), 0, 3)];

        std::vector<std::string> checked;
        const auto list = GetDlgItem(IDC_LX_DOWNLOAD_ITEMS).m_hWnd;
        for (size_t index = 0; index < payloads.size(); ++index) {
            if (ListView_GetCheckState(list, static_cast<int>(index))) checked.push_back(payloads[index]);
        }
        if (checked.empty()) {
            MessageBoxW(L"请至少勾选一个下载项目。", L"LX 批量下载", MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        payloads = std::move(checked);
        EndDialog(IDOK);
        return 0;
    }
    LRESULT OnCancel(WORD, WORD, HWND, BOOL&) { EndDialog(IDCANCEL); return 0; }
private:
    void SetAllChecks(bool checked) {
        const auto list = GetDlgItem(IDC_LX_DOWNLOAD_ITEMS).m_hWnd;
        for (int index = 0, count = ListView_GetItemCount(list); index < count; ++index)
            ListView_SetCheckState(list, index, checked ? TRUE : FALSE);
    }
};

} // namespace

bool show_search_dialog(SearchOptions& options, const std::vector<SourceOption>& sources) {
    search_dialog dialog;
    dialog.sources = sources;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    options = dialog.options;
    return true;
}

bool show_page_dialog(const SearchPage& state, int& page) {
    page_dialog dialog;
    dialog.state = state;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
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

bool show_url_dialog(std::string& url) {
    url_dialog dialog;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    url = dialog.value;
    return true;
}

void show_lyrics_dialog(const std::string& caption, const std::string& lyrics) {
    lyrics_dialog dialog(caption, lyrics);
    dialog.DoModal(core_api::get_main_window());
}

bool show_download_dialog(std::vector<std::string>& payloads, std::string& folder, std::string& quality) {
    download_dialog dialog;
    dialog.payloads = payloads;
    dialog.folder = folder.empty() ? std::string(g_download_folder.get().c_str()) : folder;
    dialog.quality = quality.empty() ? std::string(g_download_quality.get().c_str()) : quality;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    folder = dialog.folder;
    quality = dialog.quality;
    g_download_folder = folder.c_str();
    g_download_quality = quality.c_str();
    payloads = std::move(dialog.payloads);
    return true;
}

} // namespace lxmusic
