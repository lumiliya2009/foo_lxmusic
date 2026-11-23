param([ValidateSet('Win32', 'x64')][string]$Platform = 'x64')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$output = Join-Path $project "build\message-regression-$Platform"
New-Item -ItemType Directory -Path $output -Force | Out-Null
$common = [IO.File]::ReadAllText((Join-Path $project 'lx_common.cpp'))
$start = $common.IndexOf('void show_message(')
$end = $common.IndexOf('void copy_text_to_clipboard(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Message implementation not found' }
$message = $common.Substring($start, $end - $start)
$ui = [IO.File]::ReadAllText((Join-Path $project 'ui.cpp'))
$start = $ui.IndexOf('LRESULT OnOK(', $ui.IndexOf('class download_dialog'))
$start = $ui.IndexOf('{', $start) + 1
$end = $ui.IndexOf('folder = dialog_text', $start)
if ($start -le 0 -or $end -lt 0) { throw 'Download validation not found' }
$validation = $ui.Substring($start, $end - $start)
# Compile the production message helper and submission gate, replacing only UI calls.
$source = @'
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
using HWND = int;
using UINT = unsigned;
constexpr UINT MB_OK = 0, MB_ICONINFORMATION = 64, MB_ICONERROR = 16;
constexpr int IDC_LX_DOWNLOAD_NAMING = 1;
static HWND owner = 0;
static UINT flags = 0;
static int calls = 0, focus = 0, checks = 0;
static std::string shown;
namespace core_api { HWND get_main_window() { return 10; } }
HWND GetLastActivePopup(HWND window) { return window + 1; }
namespace pfc { namespace stringcvt {
const char* string_wide_from_utf8(const char* text) { return text; }
} }
int MessageBoxW(HWND window, const char* text, const char*, UINT style) {
    owner = window; shown = text; flags = style; ++calls; return 1;
}
@MESSAGE@
struct Preview { bool valid = false; std::string error; };
struct Dialog {
    std::shared_ptr<Preview> preview = std::make_shared<Preview>();
    struct Control { void SetFocus() { ++focus; } };
    Control GetDlgItem(int) { return {}; }
    void MessageBoxW(const char* text, const wchar_t*, UINT style) { ::MessageBoxW(20, text, "", style); }
    int Submit() { @VALIDATION@ return 1; }
};
void check(bool value, const char* message) { ++checks; if (!value) throw std::runtime_error(message); }
int main() {
    try {
        show_message("请先导入音源", "LX 在线音乐", MB_ICONINFORMATION);
        check(calls == 1 && owner == 11 && shown == "请先导入音源" && flags == MB_ICONINFORMATION,
            "information message lost its owner, text or OK style");
        show_message("播放失败", "LX 在线音乐", MB_ICONERROR);
        check(calls == 2 && flags == MB_ICONERROR, "error message lost its error icon");
        Dialog dialog;
        dialog.preview->error = "未知命名字段";
        check(dialog.Submit() == 0, "invalid naming started a download");
        check(calls == 3 && shown == "未知命名字段" && focus == 1, "invalid naming was not explained or focused");
        dialog.preview->error.clear();
        check(dialog.Submit() == 0 && calls == 4 && shown == "正在生成文件名示例，请稍候。",
            "unfinished validation started a download or produced an empty message");
        dialog.preview->valid = true;
        check(dialog.Submit() == 1 && calls == 4, "valid naming was blocked or displayed an extra message");
        std::cout << checks << " message/submission regression checks passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'regression.cpp'), $source.Replace('@MESSAGE@', $message).Replace('@VALIDATION@', $validation))
$testProject = @'
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Release|Win32"><Configuration>Release</Configuration><Platform>Win32</Platform></ProjectConfiguration><ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
  <PropertyGroup Label="Globals"><WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration"><ConfigurationType>Application</ConfigurationType><PlatformToolset>v145</PlatformToolset></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
  <PropertyGroup><OutDir>$(ProjectDir)</OutDir><IntDir>$(ProjectDir)obj\</IntDir><TargetName>regression</TargetName></PropertyGroup>
  <ItemDefinitionGroup><ClCompile><LanguageStandard>stdcpp17</LanguageStandard><AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions><ExceptionHandling>Sync</ExceptionHandling></ClCompile><Link><SubSystem>Console</SubSystem></Link></ItemDefinitionGroup>
  <ItemGroup><ClCompile Include="regression.cpp" /></ItemGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
'@
[IO.File]::WriteAllText((Join-Path $output 'regression.vcxproj'), $testProject)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
& (Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe') (Join-Path $output 'regression.vcxproj') /p:Configuration=Release "/p:Platform=$Platform" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Message regression build failed' }
& (Join-Path $output 'regression.exe')
if ($LASTEXITCODE -ne 0) { throw 'Message regression failed' }
