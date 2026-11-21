# Changelog

## 0.1.13

- 榜单结果只显示榜单名称，不再附加平台、收听指数或其他文本。
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
