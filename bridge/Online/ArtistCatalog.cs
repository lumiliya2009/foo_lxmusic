using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    public static class ArtistCatalog
    {
        public static OnlineCollectionPage Search(OnlinePlatform platform, string query, int page, int limit)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("请输入歌手名");
            if (ReferenceEquals(platform, OnlinePlatform.Qq)) return SearchQq(query, page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Netease)) return SearchNetease(query, page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Kugou)) return SearchKugou(query, page, limit);
            throw new InvalidOperationException("该站点暂不支持歌手搜索");
        }

        public static OnlineSearchPage Tracks(OnlineCollection artist, int page, int limit)
        {
            if (ReferenceEquals(artist.Platform, OnlinePlatform.Qq)) return QqTracks(artist, page, limit);
            if (ReferenceEquals(artist.Platform, OnlinePlatform.Netease)) return NeteaseTracks(artist, page, limit);
            if (ReferenceEquals(artist.Platform, OnlinePlatform.Kugou)) return KugouTracks(artist, page, limit);
            throw new InvalidOperationException("该站点暂不支持读取歌手歌曲");
        }

        private static OnlineCollectionPage SearchQq(string query, int page, int limit)
        {
            var root = Json(OnlineHttp.Query("https://c.y.qq.com/soso/fcgi-bin/client_search_cp", new Dictionary<string, object>
            {
                ["format"] = "json", ["p"] = Math.Max(1, page), ["n"] = Math.Max(1, Math.Min(50, limit)),
                ["w"] = query, ["t"] = 9
            }), "https://y.qq.com/");
            var list = root.SelectToken("data.singer.list") as JArray ?? new JArray();
            var collections = list.OfType<JObject>().Select(item =>
            {
                var id = Value(item, "singermid", "singerMID", "mid");
                var name = TextUtil.HtmlDecode(Value(item, "singername", "singerName", "name"));
                if (id.Length == 0 || name.Length == 0) return null;
                return new OnlineCollection { Platform = OnlinePlatform.Qq, Id = id, Title = name,
                    Subtitle = "歌手", ArtworkUrl = Value(item, "singerPic", "pic"), Kind = OnlineCollectionKind.Category };
            }).Where(item => item != null).ToList();
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(page, limit, root.SelectToken("data.singer.totalnum")?.Value<int>(), list.Count);
        }

        private static OnlineCollectionPage SearchNetease(string query, int page, int limit)
        {
            var root = Json(OnlineHttp.Query("https://music.163.com/api/cloudsearch/pc", new Dictionary<string, object>
            {
                ["s"] = query, ["type"] = 100, ["offset"] = (Math.Max(1, page) - 1) * limit,
                ["limit"] = Math.Max(1, Math.Min(50, limit))
            }), "https://music.163.com/");
            var list = root.SelectToken("result.artists") as JArray ?? new JArray();
            var collections = list.OfType<JObject>().Select(item =>
            {
                var id = Value(item, "id");
                var name = Value(item, "name");
                if (id.Length == 0 || name.Length == 0) return null;
                return new OnlineCollection { Platform = OnlinePlatform.Netease, Id = id, Title = name,
                    Subtitle = "歌手", ArtworkUrl = Value(item, "picUrl", "img1v1Url"), Kind = OnlineCollectionKind.Category,
                    TrackCount = item.Value<int?>("musicSize") ?? 0 };
            }).Where(item => item != null).ToList();
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(page, limit, root.SelectToken("result.artistCount")?.Value<int>(), list.Count);
        }

        private static OnlineCollectionPage SearchKugou(string query, int page, int limit)
        {
            var root = Json(OnlineHttp.Query("https://msearchretry.kugou.com/api/v3/search/singer", new Dictionary<string, object>
            {
                ["keyword"] = query, ["page"] = Math.Max(1, page), ["pagesize"] = Math.Max(1, Math.Min(50, limit)),
                ["showtype"] = 10, ["version"] = 7910
            }), "https://www.kugou.com/");
            // The singer search now returns the list directly in "data"; older responses used "data.info".
            var list = root["data"] as JArray ?? root.SelectToken("data.info") as JArray ?? new JArray();
            var collections = list.OfType<JObject>().Select(item =>
            {
                var id = Value(item, "singerid", "singer_id", "id");
                var name = TextUtil.HtmlDecode(Value(item, "singername", "singer_name", "name"));
                if (id.Length == 0 || name.Length == 0) return null;
                return new OnlineCollection { Platform = OnlinePlatform.Kugou, Id = id, Title = name,
                    Subtitle = "歌手", ArtworkUrl = Value(item, "imgurl", "image").Replace("{size}", "400"),
                    Kind = OnlineCollectionKind.Category, TrackCount = item.Value<int?>("songcount") ?? 0 };
            }).Where(item => item != null).ToList();
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(page, limit, root.SelectToken("data.total")?.Value<int>(), list.Count);
        }

        private static OnlineSearchPage QqTracks(OnlineCollection artist, int page, int limit)
        {
            // fcg_v8_singer_track_cp.fcg now returns 404; musicu's GetSingerSongList (order 1 = most played) replaces it.
            var root = Json("https://u.y.qq.com/cgi-bin/musicu.fcg", "https://y.qq.com/", new JObject
            {
                ["comm"] = new JObject { ["ct"] = 24, ["cv"] = 0 },
                ["req"] = new JObject
                {
                    ["module"] = "musichall.song_list_server", ["method"] = "GetSingerSongList",
                    ["param"] = new JObject
                    {
                        ["singerMid"] = artist.Id, ["order"] = 1, ["begin"] = (Math.Max(1, page) - 1) * limit,
                        ["num"] = Math.Max(1, Math.Min(100, limit))
                    }
                }
            });
            if (root.SelectToken("req.code")?.Value<int>() != 0) throw new InvalidOperationException("QQ音乐歌手歌曲读取失败");
            var list = root.SelectToken("req.data.songList") as JArray ?? new JArray();
            var tracks = new List<OnlineTrack>();
            foreach (var token in list.OfType<JObject>())
            {
                var item = token["songInfo"] as JObject ?? token;
                var track = OnlineMusicRepository.ParseQqTrack(item);
                if (track != null) tracks.Add(track);
            }
            var total = root.SelectToken("req.data.totalNum")?.Value<int>() ?? tracks.Count;
            return Page(tracks, page, limit, total);
        }

        private static OnlineSearchPage NeteaseTracks(OnlineCollection artist, int page, int limit)
        {
            var root = Json("https://music.163.com/api/v1/artist/" + Uri.EscapeDataString(artist.Id), "https://music.163.com/");
            var list = root["hotSongs"] as JArray ?? root["songs"] as JArray ?? new JArray();
            var all = list.OfType<JObject>().Select(OnlineMusicRepository.ParseNeteaseTrack).Where(item => item != null).ToList();
            return Slice(all, page, limit);
        }

        private static OnlineSearchPage KugouTracks(OnlineCollection artist, int page, int limit)
        {
            var root = Json(OnlineHttp.Query("https://mobiles.kugou.com/api/v3/singer/song", new Dictionary<string, object>
            {
                ["singerid"] = artist.Id, ["page"] = Math.Max(1, page), ["pagesize"] = Math.Max(1, Math.Min(100, limit)),
                ["sorttype"] = 2, ["version"] = 9108
            }), "https://www.kugou.com/");
            var list = root.SelectToken("data.info") as JArray ?? new JArray();
            var tracks = list.OfType<JObject>().Select(OnlineMusicRepository.ParseKugouTrack).Where(item => item != null).ToList();
            var total = root.SelectToken("data.total")?.Value<int>() ?? tracks.Count;
            return Page(tracks, page, limit, total);
        }

        private static OnlineSearchPage Slice(List<OnlineTrack> all, int page, int limit)
        {
            var start = (Math.Max(1, page) - 1) * limit;
            var tracks = all.Skip(start).Take(limit).ToList();
            return new OnlineSearchPage { Tracks = tracks, Page = Math.Max(1, page), Total = all.Count, HasNext = start + tracks.Count < all.Count };
        }

        private static OnlineSearchPage Page(List<OnlineTrack> tracks, int page, int limit, int total) =>
            new OnlineSearchPage { Tracks = tracks, Page = Math.Max(1, page), Total = total,
                HasNext = (long)Math.Max(1, page) * limit < total };

        private static JObject Json(string url, string referer, JObject body = null)
        {
            var headers = new Dictionary<string, string> { ["User-Agent"] = OnlineHttp.BrowserUserAgent, ["Referer"] = referer };
            if (body != null) headers["Content-Type"] = "application/json";
            var response = OnlineHttp.Execute(url, body == null ? "GET" : "POST", headers,
                body == null ? null : Encoding.UTF8.GetBytes(body.ToString(Formatting.None)), maxBytes: 8 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException("在线服务返回 HTTP " + response.StatusCode);
            return JObject.Parse(response.Body.TrimStart('\uFEFF'));
        }

        private static string Value(JObject item, params string[] names)
        {
            foreach (var name in names)
            {
                var value = item[name]?.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }
    }
}
