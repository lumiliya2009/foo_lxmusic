# Managed bridge

The bridge implements catalog queries, source-script execution, lyrics and downloads. The native component launches it as a separate process; requests use Base64URL-encoded arguments, and responses are tab-separated records on stdout.

This directory is licensed under **GPL-3.0-or-later**, except for files carrying separate notices. `Resources/user-api-preload.js` is derived from LX Music Mobile v1.8.4 and retains its Apache-2.0 license. See `LICENSE` and the parent directory's `THIRD_PARTY_NOTICES.md`.

Managed dependencies are embedded in `foo_lx_bridge.exe` and loaded on demand. Deploy the executable and its `.config` together; separate dependency DLLs are not required. The component package keeps the corresponding license notices.

Build:

```powershell
dotnet build ./foo_lx_bridge.csproj -c Release -p:RestoreLockedMode=true
```

Runtime user data is stored under the profile directory passed by the native component. Source scripts and account data are user-specific runtime files and are not part of this repository.

## Catalog endpoints and LX Music

Imported LX sources only resolve playback URLs (`musicUrl`). Search, albums, artists, playlists and charts call the platforms directly, as LX Music itself does. When a platform changes an endpoint, compare with the matching module in LX Music Mobile (`src/utils/musicSdk/<platform>/`, last compared with v1.9.2) and run the live checks:

```powershell
dotnet run --project ../tests/QualityRegression.csproj -c Release -- --live
```

| Feature | Platform | Bridge endpoint | LX Music Mobile 1.9.2 |
|---|---|---|---|
| Song search | QQ | `c.y.qq.com/soso/fcgi-bin/client_search_cp` | `tx/musicSearch.js`: musicu `DoSearchForQQMusicDesktop` |
| | Kugou | `songsearch.kugou.com/song_search_v2` | same |
| | NetEase | `music.163.com/api/cloudsearch/pc` | `wy/musicSearch.js`: eapi `/api/search/song/list/page` |
| | Kuwo / Migu | `search.kuwo.cn/r.s`, `jadeite.migu.cn` v3 `searchAll` | same |
| Album | QQ | `fcg_v8_album_info_cp.fcg` | none |
| | Kugou | `msearchretry.kugou.com/api/v3/search/album`, `mobiles.kugou.com/api/v3/album/song` | `kg/album.js` (same album songs) |
| | NetEase | `music.163.com/api/v1/album` | none |
| Artist | QQ | musicu `musichall.song_list_server.GetSingerSongList` | none |
| | Kugou | `msearchretry.kugou.com/api/v3/search/singer`, `mobiles.kugou.com/api/v3/singer/song` | `kg/singer.js`: `mobiles.kugou.com/api/v5/singer/song` |
| | NetEase | `music.163.com/api/v1/artist/{id}` | none |
| Playlist detail | QQ | `fcg_ucc_getcdinfo_byids_cp.fcg` | same (`tx/songList.js`) |
| | Kugou | `mobiles.kugou.com/api/v5/special/info_v2`, `song_v2` | `kg/songList.js` (same info_v2) |
| | NetEase | `music.163.com/api/v6/playlist/detail` | `wy/songList.js`: linuxapi `/api/v3/playlist/detail` |
| | Kuwo | `nplserver.kuwo.cn/pl.svc` | same |
| Featured / categories | QQ | musicu `GetRecommendWhole`, `get_all_categories`, `get_category_content` | same (`tx/songList.js`) |
| | Kugou | `m.kugou.com/plist/index` | `kg/songList.js`: `www2.kugou.kugou.com/yueku/v9/special/getSpecial` |
| | NetEase | `music.163.com/api/playlist/list`, `/catalogue` | `wy/songList.js`: weapi, same paths |
| | Kuwo | `wapi.kuwo.cn/api/pc/classify/playlist/*` | same |
| Charts | QQ | `fcg_myqq_toplist.fcg`, `fcg_v8_toplist_cp.fcg` | `tx/leaderboard.js`: `fcg_myqq_toplist.fcg`, musicu `ToplistInfoServer.GetDetail` |
| | Kugou | `mobiles.kugou.com/api/v3/rank/list`, `rank/song` | `kg/leaderboard.js`: `mobilecdnbj.kugou.com/api/v5/rank/list`, v3 `rank/song` |
| | NetEase | `music.163.com/api/toplist/detail` | `wy/leaderboard.js`: weapi `/toplist`, `/v3/playlist/detail` |
| | Kuwo / Migu | `kbangserver.kuwo.cn/ksong.s`, `app.c.nf.migu.cn` `querycontentbyId.do` | same |
