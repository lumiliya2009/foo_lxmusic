#include "stdafx.h"

namespace lxmusic {

namespace {

cfg_int g_search_site({ 0x628a3da1, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, 0);
cfg_int g_search_type({ 0x628a3da2, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, 0);
cfg_string g_search_keyword({ 0x628a3da3, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_download_folder({ 0x628a3da4, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "");
cfg_string g_download_quality({ 0x628a3da5, 0xab3d, 0x4da6, { 0xa4, 0x1d, 0xa6, 0x16, 0x92, 0x5b, 0xe1, 0x11 } }, "flac");

std::string dialog_text(HWND dialog, UINT id) {
    pfc::string8 value;
    uGetDlgItemText(dialog, id, value);
    return value.c_str();
}

class search_dialog : public CDialogImpl<search_dialog> {
public:
    enum { IDD = IDD_LX_SEARCH };
    SearchOptions options;

    BEGIN_MSG_MAP(search_dialog)
        MSG_WM_INITDIALOG(OnInitDialog)
        COMMAND_ID_HANDLER(IDOK, OnOK)
        COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
    END_MSG_MAP()

    BOOL OnInitDialog(CWindow, LPARAM) {
        CenterWindow(GetParent());
        auto site = CComboBox(GetDlgItem(IDC_LX_SITE));
        site.AddString(L"QQ音乐");
        site.AddString(L"酷狗音乐");
        site.AddString(L"网易云音乐");
        site.SetCurSel(std::clamp(static_cast<int>(g_search_site), 0, 2));

        auto type = CComboBox(GetDlgItem(IDC_LX_TYPE));
        type.AddString(L"单曲");
        type.AddString(L"专辑");
        type.AddString(L"歌手");
        type.AddString(L"歌单");
        type.SetCurSel(std::clamp(static_cast<int>(g_search_type), 0, 3));

        uSetDlgItemText(*this, IDC_LX_KEYWORD, g_search_keyword.get().c_str());
        CEdit(GetDlgItem(IDC_LX_KEYWORD)).SetSel(0, -1);
        GetDlgItem(IDC_LX_KEYWORD).SetFocus();
        return FALSE;
    }

    LRESULT OnOK(WORD, WORD, HWND, BOOL&) {
        options.keyword = dialog_text(*this, IDC_LX_KEYWORD);
        const auto first = options.keyword.find_first_not_of(" \t\r\n");
        options.keyword = first == std::string::npos ? std::string() :
            options.keyword.substr(first, options.keyword.find_last_not_of(" \t\r\n") - first + 1);
        if (options.keyword.empty()) {
            MessageBoxW(L"请输入搜索关键词。", L"LX 在线音乐", MB_OK | MB_ICONINFORMATION);
            GetDlgItem(IDC_LX_KEYWORD).SetFocus();
            return 0;
        }
        static const char* sites[] = { "qq", "kugou", "netease" };
        static const char* types[] = { "track", "album", "artist", "playlist" };
        const int site = std::clamp(CComboBox(GetDlgItem(IDC_LX_SITE)).GetCurSel(), 0, 2);
        const int type = std::clamp(CComboBox(GetDlgItem(IDC_LX_TYPE)).GetCurSel(), 0, 3);
        options.site = sites[site];
        options.type = types[type];
        options.quality = "flac";
        g_search_site = site;
        g_search_type = type;
        g_search_keyword = options.keyword.c_str();
        EndDialog(IDOK);
        return 0;
    }

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

bool show_search_dialog(SearchOptions& options) {
    search_dialog dialog;
    if (dialog.DoModal(core_api::get_main_window()) != IDOK) return false;
    options = dialog.options;
    return true;
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
