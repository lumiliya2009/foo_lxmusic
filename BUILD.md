# 构建与开发

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
dotnet run --project ./tests/QualityRegression.csproj -c Release
dotnet run --project ./tests/UpdateRegression.csproj -c Release
./tests/run-input-regression.ps1 -Platform Win32
./tests/run-input-regression.ps1 -Platform x64
```

测试覆盖音质回退、采样率等播放信息、播放失败和取消、错误 MIME 类型、专辑分页、封面回退、下载中断以及文件保留。可选联网验证及宿主 UI 检查见 `tests/README.md`。反馈问题时请提供 foobar2000 版本、组件版本、平台和错误提示，勿附带音源订阅地址、账号 Cookie 或带凭据的播放链接。

## 项目结构与许可

- 根目录 C++ 文件：foobar2000 原生界面、播放与命令，MIT 许可。
- `bridge/`：独立运行的 .NET Framework 音源桥接程序，GPL-3.0-or-later；兼容层资源保留 Apache-2.0 许可。
- `tests/`：自动回归用例及隔离宿主测试准备脚本。
- `licenses/`、`THIRD_PARTY_NOTICES.md`：第三方许可证与来源说明。

LX 脚本兼容层派生自 LX Music Mobile 1.8.4。各模块和依赖保留各自的许可证，根目录 MIT 许可不替代桥接程序或第三方代码的许可。版本变更见 `CHANGELOG.md`。
