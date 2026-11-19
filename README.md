# LX Online Music for foobar2000

导入 LX Music 自定义音源脚本，在 foobar2000 中搜索、播放和下载在线音乐。组件名：`foo_lxmusic`。

本项目只提供播放器组件与音源接口，不附带音源脚本、账号、订阅或音乐文件。

## 功能

- QQ 音乐、酷狗音乐、网易云音乐站点搜索。
- 单曲、专辑、歌手、歌单搜索；结果写入 foobar2000 播放列表。
- 专辑、歌手、歌单结果按 `Enter` 或双击直接打开；歌曲按 `Enter` 或双击播放，右键仍支持批量打开。
- 搜索记住上次的关键词、站点和类型；搜索或打开集合后自动选中并定位第一项。
- 在线播放失败会停止播放并提示歌曲与原因，不再连续跳过失败歌曲；主动停止或切歌不弹错误提示。
- 按实际音频文件头识别 FLAC、Ogg、WAV 和 MP4，兼容媒体服务器错误标注音频类型的链接。
- 默认 FLAC，可在歌曲右键菜单切换 128K、320K、FLAC、Hi-Res/24bit；切换后从原播放位置续播。
- 播放或下载时，所选音质不可用会自动回退到该歌曲的最高可用音质。
- 批量下载前可逐首勾选并选择音质；下载在后台运行，显示歌曲总进度与当前文件进度。
- 专辑结果可直接右键选择“下载整张专辑（含封面）…”，无需先展开或全选歌曲。选择目录和音质后，自动读取全部曲目并保存到“歌手 - 专辑”文件夹，歌曲按曲序命名。
- 专辑封面独立保存在同一文件夹（通常为 `cover.jpg` 或 `cover.png`），保留图片原格式；封面失败会单独提示，歌曲仍继续下载。封面不额外嵌入音频文件。
- 下载记住上次的目录和音质；重名专辑和文件自动另存，已有文件不会被覆盖。
- 获取所选歌曲歌词；只读文本窗口支持 `Esc` 关闭。
- “获取所选歌曲歌词”是标准主菜单命令，可在 foobar2000 的键盘快捷键页绑定 `Shift+Enter`。

## 安装包

- `fb1-x86`：foobar2000 v1.x 32 位。
- `fb2-x64`：foobar2000 v2.x 64 位。

安装对应 `.fb2k-component` 后重启 foobar2000。菜单位于“文件 → LX 在线音乐”，歌曲操作位于播放列表右键菜单“LX 在线音乐”。

需要 Windows 上的 .NET Framework 4.8。LX 音源脚本属于可执行代码，请只导入可信来源，并遵守内容提供方的许可与服务条款。

## 从源码构建

准备 Windows、Visual Studio C++/ATL 构建工具、Windows SDK、.NET SDK 和 .NET Framework 4.8 Developer Pack。当前验证环境使用 MSVC `v145`。

另外取得 foobar2000 SDK 2025-03-07 和 WTL 9.10。本仓库只包含组件本身，不包含 SDK 或 WTL。将本仓库放在 SDK 的 `foobar2000/foo_lxmusic` 目录，确保 `atlapp.h` 等 WTL 头文件可通过 SDK 根目录的包含路径找到：

```text
SDK-2025-03-07/
  atlapp.h, atl*.h        # WTL headers
  pfc/
  libPPUI/
  foobar2000/
    SDK/
    helpers/
    shared/
    foobar2000_component_client/
    foo_lxmusic/          # this repository
```

在本仓库目录运行：

```powershell
./package.ps1
```

输出位于 `dist/`。使用其他已安装的工具集时可传入 `-PlatformToolset`，例如 `./package.ps1 -PlatformToolset v143`；当前回归构建验证的是 `v145`。原生项目和 SDK 引用项目会使用同一个指定工具集。

只编译桥接程序不需要 foobar2000 SDK：

```powershell
dotnet build ./bridge/foo_lx_bridge.csproj -c Release -p:RestoreLockedMode=true
```

## 测试

```powershell
dotnet run --project ./tests/AlbumDownloadRegression.csproj -c Release
./tests/run-input-regression.ps1 -Platform Win32
./tests/run-input-regression.ps1 -Platform x64
```

测试覆盖播放失败和取消、错误 MIME 类型、专辑分页、封面回退、下载中断以及文件保留。可选联网验证及宿主 UI 检查见 `tests/README.md`。反馈问题时请提供 foobar2000 版本、组件版本、平台和错误提示，勿附带音源订阅地址、账号 Cookie 或带凭据的播放链接。

## 项目结构与许可

- 根目录 C++ 文件：foobar2000 原生界面、播放与命令，MIT 许可。
- `bridge/`：独立运行的 .NET Framework 音源桥接程序，GPL-3.0-or-later；兼容层资源保留 Apache-2.0 许可。
- `tests/`：自动回归用例及隔离宿主测试准备脚本。
- `licenses/`、`THIRD_PARTY_NOTICES.md`：第三方许可证与来源说明。

在线音乐实现移植自 TzPlayer，LX 脚本兼容层派生自 LX Music Mobile 1.8.4。各模块和依赖保留各自的许可证，根目录 MIT 许可不替代桥接程序或第三方代码的许可。版本变更见 `CHANGELOG.md`。
