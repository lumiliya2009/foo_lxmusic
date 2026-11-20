# 适用于 foobar2000 的 LX 在线音乐组件

支持多音源切换、在线搜索与播放、歌词、音质回退、专辑和封面下载。

## 下载

- [foobar2000 v1.x · 32 位](https://github.com/lumiliya2009/foo_lxmusic/releases/download/v0.1.8/foo_lxmusic-0.1.8-fb1-x86.fb2k-component)
- [foobar2000 v2.x · 64 位](https://github.com/lumiliya2009/foo_lxmusic/releases/download/v0.1.8/foo_lxmusic-0.1.8-fb2-x64.fb2k-component)

## 使用

安装后重启，在“文件 → LX 在线音乐 → 管理音源”导入脚本，搜索时选择音源和站点。回车打开或播放，退格返回上一层，右键下载。

列表末尾可翻页、到尾页或输入页码跳转。搜索框保存最近 20 条记录，直接输入覆盖旧文字；多次搜索保留独立结果列表。

改返回键：在“首选项 → 快捷键”新增绑定，动作选“文件 → LX 在线音乐 → 返回上一层”。自定义后默认退格停用。

菜单“检查更新”可下载对应位数的安装包，确认后自动重启安装，替换失败会恢复原文件。

需要 .NET Framework 4.8，音源需自行导入。

[构建说明](BUILD.md) · [更新记录](CHANGELOG.md) · [许可证与来源](THIRD_PARTY_NOTICES.md)
