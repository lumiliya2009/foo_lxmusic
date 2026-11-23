# Changelog

## 未发布

- 将 popup 通知统一为 Windows 信息框，保留输入型对话框。
- 需求署名：泡泡堂。

## 0.1.18

- 修复 QQ 音乐、酷狗音乐打开歌手时报 HTTP 404，以及酷狗搜索歌手没有结果的问题。
- 修复 QQ 音乐精选歌单和分类歌单打开后没有歌曲的问题，歌单广场改用与洛雪音乐一致的接口。
- 按 QQ 号获取歌单时改为读取当前的“我喜欢”，并列出该账号收藏的歌单。
- 移除“搜索结果按下载命名模板显示”选项，歌曲标题交给 foobar2000 主题显示。
- 自动更新完成后清理残留的安装文件；下载更新失败时显示具体原因。
- 批量切换音质不再卡顿；搜索翻页时不再反复重写音源库。

## 0.1.17

- 支持按 QQ 号或分享链接浏览公开歌单及“我喜欢”，并保持歌单原顺序。
- 歌曲搜索结果可选择每次新建播放列表，或固定替换指定播放列表。
- 桥接依赖改为内嵌加载，避免部分安装环境缺少 DLL；感谢 @xq1666 提供的方案。

## 0.1.16

- 单页结果不再显示页数跳转。
- 优化浏览列表及窗口焦点恢复。

## 0.1.15

- 专辑、歌手、歌单和榜单改用独立浏览列表，去除多余占位字符；进入歌曲时自动切回 foobar2000 播放列表，支持返回原浏览位置。
- 修复关闭下载窗口或返回上层时，浏览列表偶尔失去焦点的问题。

## 0.1.14

- 完善更新流程，继续修复一些可恶的 Bug。
- 感谢某个可恶的人类提供的更新解决方案，让本项目不再屈服于 Gitee。

## 0.1.13

- 榜单结果只显示榜单名称，不再附加平台、收听指数、多余分隔符或其他文本。
- 支持右键批量下载整个榜单，并沿用并发下载、失败跳过和查重设置。
- 自动更新优先使用实测更快的镜像，减少国内下载等待。
- 普通歌曲及专辑、歌手、歌单结果的艺术家和专辑信息保持不变。
- 隐藏主菜单中的翻页、歌词和返回命令，快捷键绑定能力继续保留。
- 新增“发布页”菜单。
- 修复自动更新误判非组件文件和 ZIP 路径无效的问题。
- 修复其他问题。

## 0.1.12

- 支持了咪咕和汽水音乐的搜索和播放。
- 新增“随便看看”，可按音源和站点浏览榜单；支持站点提供精选歌单和分类入口。
- 咪咕、酷我、汽水等站点只在当前音源实际支持时出现在浏览与搜索选项中。
- 搜索结果可选套用下载命名模板，默认关闭。
- 上一页、下一页、尾页、页数跳转均可绑定快捷键，并清理分页行的缺失元数据占位符。
- 修复其他问题。

## 0.1.11

- Edit imported source scripts directly, including configured cookies.
- Remove the confirmation dialog shown before importing a source.
- Show source version, author, description, supported platforms and qualities.
- Check paid-source updates through LX `updateAlert`, or open the provided update page.
- Copy the address of a source imported from a URL.
- Keep the source manager open after copying a source address.
- Show stage-by-stage status while checking for source updates.
- Use status messages for update checks and direct-link copies.
- Add open-folder and close buttons to the download completion dialog.
- Ask for confirmation before source updates and show the result in a dialog.
- Move source actions into the source-list context menu.
- Download future component updates from jsDelivr mirrors first and GitHub as the final fallback.

## 0.1.10

- Download all search pages with configurable parallel tasks, failure skipping and post-download duplicate cleanup.
- Add a preferences page and optional compact download dialog.
- Check for component updates automatically with fallback feeds and connection routes.
- Copy a selected track's playback URL from its context menu.

## 0.1.9

- Choose or customize download filenames with a preview and saved preferences.

## 0.1.8

- Add search pagination and page jumps in result lists.
- Save search history and keep multiple result lists.
- Download updates from GitHub and install them on restart, with package verification and rollback.

## 0.1.7

- Save multiple sources, switch them in search, and list supported searchable platforms.
- Add source management, Kuwo/Migu track search and a GitHub update check.
- Improve LX script compatibility and fix playback fallback with decoders built without RTTI.

## 0.1.6

- Show the decoder's actual sample rate, bitrate, channels and codec during playback.
- Try other available qualities of the same song when its media URL is rejected or invalid, resuming at the current position when possible.
- Stop with one error after available qualities fail; avoid repeating failed qualities and URLs.

## 0.1.5

- Download an entire album directly from its context menu, including all catalog pages.
- Store albums in separate directories with ordered track filenames and a cover image.
- Remember download directory and quality; preserve existing files and directories.

## 0.1.4

- Detect FLAC, Ogg, WAV and MP4 from file bytes before selecting a decoder, including FLAC streams incorrectly labeled as Ogg by the server.

## 0.1.3

- Open album, artist and playlist results with Enter or double-click.
- Stop and show an error when online playback fails, without automatically skipping failed songs.
- Remember search options and focus the first result.

## 0.1.2

- Search online tracks and collections, import LX sources, switch playback quality, retrieve lyrics and download selected tracks.

## Source publication preparation

- Include build instructions, dependency locks and component-specific license notices.
- Exclude local profiles, downloaded media, imported source configuration and build artifacts from the source repository.
