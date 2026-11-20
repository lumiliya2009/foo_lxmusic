using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    /// <summary>
    /// 专辑 / 在线歌单的搜索与曲目读取（对齐参考实现的 OfficialMusicCatalog）。
    /// 歌曲搜索仍走 OnlineMusicRepository.Search。
    /// </summary>
    public static class OnlineCollectionSearch
    {
        private const string QqSearch = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";
        private const string QqPlaylistSearch = "https://c.y.qq.com/soso/fcgi-bin/client_music_search_songlist";
        private const string QqAlbumDetail = "https://c.y.qq.com/v8/fcg-bin/fcg_v8_album_info_cp.fcg";
        private const string NeteaseSearch = "https://music.163.com/api/cloudsearch/pc";
        private const string NeteaseAlbumDetail = "https://music.163.com/api/v1/album";
        private const string KugouAlbumSearch = "https://msearchretry.kugou.com/api/v3/search/album";
        private const string KugouPlaylistSearch = "https://msearchretry.kugou.com/api/v3/search/special";
        private const string KugouAlbumDetail = "https://mobiles.kugou.com/api/v3/album/song";
        private const int PageSize = 100;

        private const string QqPlaylistSearchUserAgent =
            "Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; WOW64; Trident/5.0)";

        // ------------------------------------------------------------- 搜索

        public static OnlineCollectionPage Search(OnlinePlatform platform, OnlineSearchType type, string query, int page, int limit)
        {
            if (type == null || ReferenceEquals(type, OnlineSearchType.Tracks) || type.IsBrowse)
                throw new InvalidOperationException("按类型搜索只支持专辑或在线歌单");
            var clean = (query ?? string.Empty).Trim();
            if (clean.Length == 0) throw new InvalidOperationException("请输入搜索内容");
            if (ReferenceEquals(platform, OnlinePlatform.Qq)) return SearchQq(type, clean, page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Netease)) return SearchNetease(type, clean, page, limit);
            if (platform == OnlinePlatform.Kugou) return SearchKugou(type, clean, page, limit);
            throw new InvalidOperationException("该站点目前支持单曲搜索");
        }

        private static OnlineCollectionPage SearchQq(OnlineSearchType type, string query, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var albums = ReferenceEquals(type, OnlineSearchType.Albums);
            JObject root;
            if (albums)
            {
                root = GetJson(OnlineHttp.Query(QqSearch, new Dictionary<string, object>
                {
                    ["format"] = "json", ["p"] = safePage, ["n"] = safeLimit, ["w"] = query, ["t"] = 8
                }), QqHeaders(), 4 * 1024 * 1024);
            }
            else
            {
                root = GetJson(OnlineHttp.Query(QqPlaylistSearch, new Dictionary<string, object>
                {
                    ["page_no"] = safePage - 1, ["num_per_page"] = safeLimit, ["format"] = "json",
                    ["query"] = query, ["remoteplace"] = "txt.yqq.playlist",
                    ["inCharset"] = "utf8", ["outCharset"] = "utf-8"
                }), new Dictionary<string, string>
                {
                    ["User-Agent"] = QqPlaylistSearchUserAgent,
                    ["Referer"] = "http://y.qq.com/portal/search.html"
                }, 4 * 1024 * 1024);
            }
            if (root.Value<int?>("code") != 0) throw new InvalidOperationException("QQ音乐搜索失败");
            var data = root["data"] as JObject ?? new JObject();
            var container = albums ? data["album"] as JObject ?? new JObject() : data;
            var list = container["list"] as JArray ?? new JArray();
            var kind = albums ? OnlineCollectionKind.Album : OnlineCollectionKind.Playlist;

            var result = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>(albums ? "albumMID" : "dissid");
                var title = TextUtil.HtmlDecode(item.Value<string>(albums ? "albumName" : "dissname"));
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                var subtitle = albums
                    ? TextUtil.HtmlDecode(item.Value<string>("singerName"))
                    : TextUtil.HtmlDecode((item["creator"] as JObject)?.Value<string>("name"));
                var artwork = item.Value<string>(albums ? "albumPic" : "imgurl");
                result.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Qq,
                    Id = id,
                    Title = title,
                    Subtitle = subtitle ?? string.Empty,
                    ArtworkUrl = Https(artwork),
                    Kind = kind,
                    TrackCount = PositiveCount(item, "song_cnt", "songnum", "song_count", "total_song_num")
                });
            }
            var total = albums
                ? container.Value<int?>("totalnum")
                : data.Value<int?>("sum");
            return new OnlineCollectionPage { Collections = result }.WithPagination(safePage, safeLimit, total, list.Count);
        }

        private static OnlineCollectionPage SearchNetease(OnlineSearchType type, string query, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var albums = ReferenceEquals(type, OnlineSearchType.Albums);
            var root = GetJson(OnlineHttp.Query(NeteaseSearch, new Dictionary<string, object>
            {
                ["s"] = query,
                ["type"] = albums ? 10 : 1000,
                ["offset"] = (safePage - 1) * safeLimit,
                ["limit"] = safeLimit
            }), NeteaseHeaders(), 4 * 1024 * 1024);
            if (root.Value<int?>("code") != 200) throw new InvalidOperationException("网易云音乐搜索失败");
            var result = root["result"] as JObject ?? new JObject();
            var list = result[albums ? "albums" : "playlists"] as JArray ?? new JArray();
            var kind = albums ? OnlineCollectionKind.Album : OnlineCollectionKind.Playlist;

            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>("id");
                var title = (item.Value<string>("name") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                var subtitle = albums
                    ? (item["artist"] as JObject)?.Value<string>("name")
                    : (item["creator"] as JObject)?.Value<string>("nickname");
                var artwork = item.Value<string>(albums ? "picUrl" : "coverImgUrl");
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Netease,
                    Id = id,
                    Title = title,
                    Subtitle = subtitle ?? string.Empty,
                    ArtworkUrl = Https(artwork),
                    Kind = kind,
                    TrackCount = PositiveCount(item, "trackCount", "track_count", "songCount", "song_count")
                });
            }
            var total = result.Value<int?>(albums ? "albumCount" : "playlistCount");
            return new OnlineCollectionPage { Collections = collections }.WithPagination(safePage, safeLimit, total, list.Count);
        }

        private static OnlineCollectionPage SearchKugou(OnlineSearchType type, string query, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var albums = ReferenceEquals(type, OnlineSearchType.Albums);
            var root = GetJson(OnlineHttp.Query(albums ? KugouAlbumSearch : KugouPlaylistSearch,
                new Dictionary<string, object>
                {
                    ["keyword"] = query, ["page"] = safePage, ["pagesize"] = safeLimit,
                    ["showtype"] = 10, ["filter"] = 0, ["version"] = 7910, ["sver"] = 2
                }), KugouHeaders(), 4 * 1024 * 1024);
            if (root.Value<int?>("errcode") != 0) throw new InvalidOperationException("酷狗音乐搜索失败");
            var data = root["data"] as JObject ?? new JObject();
            var list = data["info"] as JArray ?? new JArray();
            var kind = albums ? OnlineCollectionKind.Album : OnlineCollectionKind.Playlist;

            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>(albums ? "albumid" : "specialid");
                var title = TextUtil.HtmlDecode(item.Value<string>(albums ? "albumname" : "specialname"));
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                var subtitle = TextUtil.HtmlDecode(item.Value<string>(albums ? "singername" : "nickname"));
                var artwork = (item.Value<string>("imgurl") ?? string.Empty).Replace("{size}", "400");
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Kugou,
                    Id = id,
                    Title = title,
                    Subtitle = subtitle ?? string.Empty,
                    ArtworkUrl = Https(artwork),
                    Kind = kind,
                    TrackCount = PositiveCount(item, "song_count", "songcount", "track_count", "count")
                });
            }
            var total = data.Value<int?>("total");
            return new OnlineCollectionPage { Collections = collections }.WithPagination(safePage, safeLimit, total, list.Count);
        }

        // ------------------------------------------------------------- 曲目

        /// <summary>读取专辑曲目（歌单曲目走 OnlinePlaylistService）。</summary>
        public static OnlineSearchPage AlbumTracks(OnlineCollection collection, int page)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (ReferenceEquals(collection.Platform, OnlinePlatform.Qq)) return QqAlbumTracks(collection, page);
            if (ReferenceEquals(collection.Platform, OnlinePlatform.Netease)) return NeteaseAlbumTracks(collection, page);
            if (collection.Platform == OnlinePlatform.Kugou) return KugouAlbumTracks(collection, page);
            throw new InvalidOperationException("该站点暂不支持专辑展开");
        }

        private static OnlineSearchPage QqAlbumTracks(OnlineCollection collection, int page)
        {
            var safePage = Math.Max(1, page);
            var root = GetJson(OnlineHttp.Query(QqAlbumDetail, new Dictionary<string, object>
            {
                ["albummid"] = collection.Id, ["format"] = "json", ["platform"] = "yqq.json"
            }), QqHeaders(), 8 * 1024 * 1024);
            var list = (root["data"] as JObject)?["list"] as JArray ?? new JArray();
            var start = (safePage - 1) * PageSize;
            var end = Math.Min(list.Count, start + PageSize);
            var tracks = new List<OnlineTrack>();
            for (var i = start; i < end; i++)
            {
                var track = OnlineMusicRepository.ParseQqTrack(list[i] as JObject);
                if (track != null) tracks.Add(track);
            }
            var total = Math.Max(Math.Max(collection.TrackCount, list.Count), tracks.Count);
            return new OnlineSearchPage
            {
                Tracks = tracks,
                Page = safePage,
                HasNext = end < list.Count,
                Total = total
            };
        }

        private static OnlineSearchPage NeteaseAlbumTracks(OnlineCollection collection, int page)
        {
            var safePage = Math.Max(1, page);
            var root = GetJson(NeteaseAlbumDetail + "/" + collection.Id, NeteaseHeaders(), 8 * 1024 * 1024);
            var all = root["songs"] as JArray ?? new JArray();
            var start = (safePage - 1) * PageSize;
            var end = Math.Min(all.Count, start + PageSize);
            var tracks = new List<OnlineTrack>();
            for (var i = start; i < end; i++)
            {
                var track = OnlineMusicRepository.ParseNeteaseTrack(all[i] as JObject);
                if (track != null) tracks.Add(track);
            }
            return new OnlineSearchPage
            {
                Tracks = tracks,
                Page = safePage,
                HasNext = end < all.Count,
                Total = Math.Max(collection.TrackCount, all.Count)
            };
        }

        private static OnlineSearchPage KugouAlbumTracks(OnlineCollection collection, int page)
        {
            var safePage = Math.Max(1, page);
            var root = GetJson(OnlineHttp.Query(KugouAlbumDetail, new Dictionary<string, object>
            {
                ["version"] = 9108, ["albumid"] = collection.Id, ["plat"] = 0,
                ["pagesize"] = PageSize, ["area_code"] = 0, ["page"] = safePage, ["with_res_tag"] = 0
            }), KugouHeaders(), 8 * 1024 * 1024);
            var data = root["data"] as JObject ?? new JObject();
            var list = data["info"] as JArray ?? new JArray();
            var total = data.Value<int?>("total") ?? list.Count;
            var tracks = new List<OnlineTrack>();
            foreach (var item in list.OfType<JObject>())
            {
                if (string.IsNullOrWhiteSpace(item.Value<string>("album_name")))
                    item["album_name"] = collection.Title;
                var track = OnlineMusicRepository.ParseKugouTrack(item);
                if (track != null) tracks.Add(track);
            }
            return new OnlineSearchPage
            {
                Tracks = tracks,
                Page = safePage,
                HasNext = (long)safePage * PageSize < Math.Max(total, collection.TrackCount),
                Total = Math.Max(total, collection.TrackCount)
            };
        }

        // ------------------------------------------------------------- 辅助

        private static Dictionary<string, string> QqHeaders()
        {
            return new Dictionary<string, string>
            {
                ["User-Agent"] = OnlineHttp.BrowserUserAgent,
                ["Referer"] = "https://y.qq.com/"
            };
        }

        private static Dictionary<string, string> NeteaseHeaders()
        {
            return new Dictionary<string, string>
            {
                ["User-Agent"] = OnlineHttp.BrowserUserAgent,
                ["Referer"] = "https://music.163.com/"
            };
        }

        private static Dictionary<string, string> KugouHeaders()
        {
            return new Dictionary<string, string>
            {
                ["User-Agent"] = OnlineHttp.BrowserUserAgent,
                ["Referer"] = "http://www2.kugou.kugou.com/"
            };
        }

        private static JObject GetJson(string url, IDictionary<string, string> headers, int maxBytes)
        {
            var response = OnlineHttp.Execute(url, headers: headers, maxBytes: maxBytes);
            if (!response.Ok) throw new InvalidOperationException("在线服务返回 HTTP " + response.StatusCode);
            try
            {
                return JToken.Parse(response.Body.TrimStart('\uFEFF')) as JObject ?? new JObject();
            }
            catch
            {
                throw new InvalidOperationException("在线服务返回了无法解析的内容");
            }
        }

        private static int PositiveCount(JObject item, params string[] names)
        {
            foreach (var name in names)
            {
                var value = item.Value<int?>(name);
                if (value.HasValue && value.Value > 0) return value.Value;
            }
            return 0;
        }

        private static string Https(string value)
        {
            return (value ?? string.Empty).Trim().Replace("http://", "https://");
        }
    }
}
