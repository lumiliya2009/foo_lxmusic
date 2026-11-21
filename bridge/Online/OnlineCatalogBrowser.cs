using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    /// <summary>QQ、酷狗和网易云的公开榜单、精选歌单及分类目录。</summary>
    public static class OnlineCatalogBrowser
    {
        public static bool SupportsRankings(OnlinePlatform platform) =>
            ReferenceEquals(platform, OnlinePlatform.Qq) ||
            ReferenceEquals(platform, OnlinePlatform.Kugou) ||
            ReferenceEquals(platform, OnlinePlatform.Netease) ||
            ReferenceEquals(platform, OnlinePlatform.Kuwo) ||
            ReferenceEquals(platform, OnlinePlatform.Migu);

        public static bool SupportsFeaturedPlaylists(OnlinePlatform platform) =>
            ReferenceEquals(platform, OnlinePlatform.Qq) ||
            ReferenceEquals(platform, OnlinePlatform.Kugou) ||
            ReferenceEquals(platform, OnlinePlatform.Netease) ||
            ReferenceEquals(platform, OnlinePlatform.Kuwo);

        public static bool SupportsCategories(OnlinePlatform platform) =>
            ReferenceEquals(platform, OnlinePlatform.Qq) ||
            ReferenceEquals(platform, OnlinePlatform.Netease) ||
            ReferenceEquals(platform, OnlinePlatform.Kuwo);

        public static string Capabilities(OnlinePlatform platform)
        {
            var values = new List<string> { "track" };
            if (platform.SupportsCollections) values.AddRange(new[] { "album", "artist", "playlist" });
            if (SupportsFeaturedPlaylists(platform)) values.Add("featured");
            if (SupportsRankings(platform)) values.Add("rankings");
            if (SupportsCategories(platform)) values.Add("categories");
            if (SupportsRankings(platform) || ReferenceEquals(platform, OnlinePlatform.Qishui)) values.Add("discover");
            return string.Join(",", values);
        }

        public static OnlineCollectionPage Rankings(OnlinePlatform platform, int page, int limit)
        {
            if (ReferenceEquals(platform, OnlinePlatform.Qq)) return QqRankings(page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Kugou)) return KugouRankings(page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Netease)) return NeteaseRankings(page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Kuwo)) return KuwoRankings(page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Migu)) return MiguRankings(page, limit);
            throw new InvalidOperationException("该站点暂不支持浏览榜单");
        }

        public static OnlineCollectionPage Categories(OnlinePlatform platform, int page, int limit)
        {
            if (ReferenceEquals(platform, OnlinePlatform.Qq)) return QqCategories(page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Netease)) return NeteaseCategories(page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Kuwo)) return KuwoCategories(page, limit);
            throw new InvalidOperationException("该站点暂不支持浏览歌单分类");
        }

        public static OnlineCollectionPage FeaturedPlaylists(OnlinePlatform platform, int page, int limit)
        {
            if (ReferenceEquals(platform, OnlinePlatform.Qq)) return QqPlaylists("10000000", page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Kugou)) return KugouPlaylists(page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Netease)) return NeteasePlaylists("全部", page, limit);
            if (ReferenceEquals(platform, OnlinePlatform.Kuwo)) return KuwoPlaylists(null, page, limit);
            throw new InvalidOperationException("该站点暂不支持浏览精选歌单");
        }

        public static OnlineCollectionPage CategoryPlaylists(OnlineCollection category, int page, int limit)
        {
            if (category == null || category.Kind != OnlineCollectionKind.Category)
                throw new InvalidOperationException("所选项目不是歌单分类");
            if (ReferenceEquals(category.Platform, OnlinePlatform.Qq)) return QqPlaylists(category.Id, page, limit);
            if (ReferenceEquals(category.Platform, OnlinePlatform.Netease)) return NeteasePlaylists(category.Id, page, limit);
            if (ReferenceEquals(category.Platform, OnlinePlatform.Kuwo)) return KuwoPlaylists(category.Id, page, limit);
            throw new InvalidOperationException("该站点暂不支持展开歌单分类");
        }

        public static OnlineSearchPage RankingTracks(OnlineCollection ranking, int page)
        {
            if (ranking == null || ranking.Kind != OnlineCollectionKind.Ranking)
                throw new InvalidOperationException("所选项目不是榜单");
            if (ReferenceEquals(ranking.Platform, OnlinePlatform.Qq)) return QqRankingTracks(ranking, page);
            if (ReferenceEquals(ranking.Platform, OnlinePlatform.Kugou)) return KugouRankingTracks(ranking, page);
            if (ReferenceEquals(ranking.Platform, OnlinePlatform.Kuwo)) return KuwoRankingTracks(ranking, page);
            if (ReferenceEquals(ranking.Platform, OnlinePlatform.Migu)) return MiguRankingTracks(ranking, page);
            if (ReferenceEquals(ranking.Platform, OnlinePlatform.Netease))
                return NeteasePlaylistLoader.Tracks(ranking.Id, Math.Max(1, page), string.Empty);
            throw new InvalidOperationException("该站点暂不支持读取榜单歌曲");
        }

        public static OnlineSearchPage DiscoverTracks(OnlinePlatform platform, int page, int limit)
        {
            if (!ReferenceEquals(platform, OnlinePlatform.Qishui))
                throw new InvalidOperationException("该站点的随便看看会显示榜单");
            page = Math.Max(1, page);
            limit = Math.Max(1, Math.Min(50, limit));
            var queries = new[] { "热歌", "流行", "华语", "新歌" };
            var merged = new List<OnlineTrack>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var query = 0; query < queries.Length; query++)
            {
                var result = ExtendedCatalog.Search(OnlinePlatform.Qishui, queries[query], 1, 50);
                var offset = (int)((uint)(DateTime.UtcNow.DayOfYear * 7 + query * 13) % (uint)Math.Max(1, result.Tracks.Count));
                foreach (var track in result.Tracks.Skip(offset).Concat(result.Tracks.Take(offset)))
                    if (seen.Add(track.Id)) merged.Add(track);
            }
            var start = (long)(page - 1) * limit;
            var tracks = start >= merged.Count
                ? new List<OnlineTrack>()
                : merged.Skip((int)start).Take(limit).ToList();
            return new OnlineSearchPage { Tracks = tracks }
                .WithPagination(page, limit, merged.Count, tracks.Count);
        }

        public static OnlineSearchPage KuwoPlaylistTracks(OnlineCollection playlist, int page)
        {
            if (playlist == null || !ReferenceEquals(playlist.Platform, OnlinePlatform.Kuwo))
                throw new InvalidOperationException("所选项目不是酷我歌单");
            var safePage = Math.Max(1, page);
            var root = GetJson(OnlineHttp.Query("https://nplserver.kuwo.cn/pl.svc", new Dictionary<string, object>
            {
                ["op"] = "getlistinfo", ["pid"] = playlist.Id, ["pn"] = safePage - 1,
                ["rn"] = 100, ["encode"] = "utf8", ["keyset"] = "pl2012",
                ["identity"] = "kuwo", ["pcmp4"] = 1, ["vipver"] = "MUSIC_9.0.5.0_W1",
                ["newver"] = 1
            }), "https://www.kuwo.cn/");
            if ((root.Value<string>("result") ?? string.Empty) != "ok")
                throw new InvalidOperationException("酷我歌单没有返回歌曲");
            var tracks = (root["musiclist"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(KuwoTrack).Where(track => track != null).ToList();
            var total = Math.Max(playlist.TrackCount, root.Value<int?>("total") ?? tracks.Count);
            return new OnlineSearchPage
            {
                Tracks = tracks, Page = safePage, Total = total, TotalKnown = total > 0,
                HasNext = (long)safePage * 100 < total
            };
        }

        private static OnlineCollectionPage QqRankings(int page, int limit)
        {
            var root = GetJson(OnlineHttp.Query("https://c.y.qq.com/v8/fcg-bin/fcg_myqq_toplist.fcg",
                new Dictionary<string, object>
                {
                    ["format"] = "json", ["g_tk"] = 5381, ["inCharset"] = "utf8",
                    ["outCharset"] = "utf-8", ["notice"] = 0, ["platform"] = "yqq.json",
                    ["needNewCode"] = 0
                }), "https://y.qq.com/");
            if (root.Value<int?>("code") != 0) throw new InvalidOperationException("QQ音乐榜单读取失败");
            var list = root.SelectToken("data.topList") as JArray ?? new JArray();
            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>("id") ?? string.Empty;
                var title = TextUtil.HtmlDecode(item.Value<string>("topTitle"));
                if (id.Length == 0 || title.Length == 0) continue;
                var plays = item.Value<long?>("listenCount") ?? 0;
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Qq,
                    Id = id,
                    Title = title,
                    Subtitle = plays > 0 ? plays.ToString("N0", CultureInfo.InvariantCulture) + " 次收听" : "QQ音乐榜单",
                    ArtworkUrl = Https(item.Value<string>("picUrl")),
                    Kind = OnlineCollectionKind.Ranking
                });
            }
            return SliceCollections(collections, page, limit);
        }

        private static OnlineCollectionPage NeteaseRankings(int page, int limit)
        {
            var root = GetJson("https://music.163.com/api/toplist/detail", "https://music.163.com/");
            if (root.Value<int?>("code") != 200) throw new InvalidOperationException("网易云音乐榜单读取失败");
            var list = root["list"] as JArray ?? new JArray();
            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>("id") ?? string.Empty;
                var title = (item.Value<string>("name") ?? string.Empty).Trim();
                if (id.Length == 0 || title.Length == 0) continue;
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Netease,
                    Id = id,
                    Title = title,
                    Subtitle = (item.Value<string>("updateFrequency") ?? "网易云音乐榜单").Trim(),
                    ArtworkUrl = Https(item.Value<string>("coverImgUrl")),
                    Kind = OnlineCollectionKind.Ranking,
                    TrackCount = item.Value<int?>("trackCount") ?? 0
                });
            }
            return SliceCollections(collections, page, limit);
        }

        private static OnlineCollectionPage KugouRankings(int page, int limit)
        {
            var root = GetJson(OnlineHttp.Query("https://mobiles.kugou.com/api/v3/rank/list",
                new Dictionary<string, object>
                {
                    ["version"] = 9108, ["plat"] = 0, ["page"] = Math.Max(1, page),
                    ["pagesize"] = Math.Max(1, Math.Min(50, limit)), ["withsong"] = 0,
                    ["parentid"] = 0, ["area_code"] = 1, ["ranktype"] = 2
                }), "https://www.kugou.com/");
            if (root.Value<int?>("errcode") != 0) throw new InvalidOperationException("酷狗榜单读取失败");
            var data = root["data"] as JObject ?? new JObject();
            var list = data["info"] as JArray ?? new JArray();
            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var rankId = item.Value<string>("rankid") ?? string.Empty;
                var rankCid = item.Value<string>("rank_cid") ?? string.Empty;
                var title = TextUtil.HtmlDecode(item.Value<string>("rankname"));
                if (title.Length == 0 || (rankId.Length == 0 && rankCid.Length == 0)) continue;
                var count = item.SelectToken("extra.resp.all_total")?.Value<int?>() ?? 0;
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Kugou,
                    Id = rankId + ":" + rankCid,
                    Title = title,
                    Subtitle = item.Value<string>("update_frequency") ?? "酷狗榜单",
                    ArtworkUrl = Https(item.Value<string>("imgurl")),
                    Kind = OnlineCollectionKind.Ranking,
                    TrackCount = count
                });
            }
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(page, limit, data.Value<int?>("total"), list.Count);
        }

        private static readonly Tuple<string, string>[] KuwoBoards =
        {
            Tuple.Create("93", "飙升榜"), Tuple.Create("16", "热歌榜"),
            Tuple.Create("145", "会员榜"), Tuple.Create("158", "抖音榜"),
            Tuple.Create("187", "趋势榜"), Tuple.Create("26", "怀旧榜"),
            Tuple.Create("104", "华语榜"), Tuple.Create("182", "粤语榜"),
            Tuple.Create("22", "欧美榜"), Tuple.Create("184", "韩语榜"),
            Tuple.Create("183", "日语榜")
        };

        private static OnlineCollectionPage KuwoRankings(int page, int limit)
        {
            var collections = KuwoBoards.Select(board => new OnlineCollection
            {
                Platform = OnlinePlatform.Kuwo,
                Id = board.Item1,
                Title = board.Item2,
                Subtitle = "酷我音乐榜单",
                Kind = OnlineCollectionKind.Ranking
            }).ToList();
            return SliceCollections(collections, page, limit);
        }

        private static readonly Tuple<string, string>[] MiguBoards =
        {
            Tuple.Create("27553319", "新歌榜"), Tuple.Create("27186466", "热歌榜"),
            Tuple.Create("27553408", "原创榜"), Tuple.Create("75959118", "音乐风向榜"),
            Tuple.Create("76557036", "彩铃分贝榜"), Tuple.Create("76557745", "会员臻爱榜"),
            Tuple.Create("23189800", "港台榜"), Tuple.Create("23189399", "内地榜"),
            Tuple.Create("19190036", "欧美榜"), Tuple.Create("83176390", "国风金曲榜")
        };

        private static OnlineCollectionPage MiguRankings(int page, int limit)
        {
            var collections = MiguBoards.Select(board => new OnlineCollection
            {
                Platform = OnlinePlatform.Migu,
                Id = board.Item1,
                Title = board.Item2,
                Subtitle = "咪咕音乐榜单",
                Kind = OnlineCollectionKind.Ranking
            }).ToList();
            return SliceCollections(collections, page, limit);
        }

        private static OnlineCollectionPage QqCategories(int page, int limit)
        {
            var root = GetJson("https://c.y.qq.com/splcloud/fcgi-bin/fcg_get_diss_tag_conf.fcg?format=json&inCharset=utf8&outCharset=utf-8",
                "https://y.qq.com/");
            if (root.Value<int?>("code") != 0) throw new InvalidOperationException("QQ音乐歌单分类读取失败");
            var collections = new List<OnlineCollection>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in (root.SelectToken("data.categories") as JArray ?? new JArray()).OfType<JObject>())
            {
                var groupName = TextUtil.HtmlDecode(group.Value<string>("categoryGroupName"));
                foreach (var item in (group["items"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var id = item.Value<string>("categoryId") ?? string.Empty;
                    var title = TextUtil.HtmlDecode(item.Value<string>("categoryName"));
                    if (id.Length == 0 || title.Length == 0 || !seen.Add(id)) continue;
                    collections.Add(new OnlineCollection
                    {
                        Platform = OnlinePlatform.Qq,
                        Id = id,
                        Title = title,
                        Subtitle = groupName,
                        Kind = OnlineCollectionKind.Category
                    });
                }
            }
            return SliceCollections(collections, page, limit);
        }

        private static OnlineCollectionPage NeteaseCategories(int page, int limit)
        {
            var root = GetJson("https://music.163.com/api/playlist/catalogue", "https://music.163.com/");
            if (root.Value<int?>("code") != 200) throw new InvalidOperationException("网易云歌单分类读取失败");
            var groups = root["categories"] as JObject ?? new JObject();
            var collections = new List<OnlineCollection>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in (root["sub"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var title = (item.Value<string>("name") ?? string.Empty).Trim();
                if (title.Length == 0 || !seen.Add(title)) continue;
                var groupId = item.Value<string>("category") ?? string.Empty;
                var groupName = groups.Value<string>(groupId) ?? "歌单分类";
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Netease,
                    Id = title,
                    Title = title,
                    Subtitle = groupName,
                    ArtworkUrl = Https(item.Value<string>("imgUrl")),
                    Kind = OnlineCollectionKind.Category,
                    TrackCount = item.Value<int?>("resourceCount") ?? 0
                });
            }
            return SliceCollections(collections, page, limit);
        }

        private static OnlineCollectionPage KuwoCategories(int page, int limit)
        {
            var root = GetJson("https://wapi.kuwo.cn/api/pc/classify/playlist/getTagList?cmd=rcm_keyword_playlist&user=0&prod=kwplayer_pc_9.0.5.0&vipver=9.0.5.0&source=kwplayer_pc_9.0.5.0&loginUid=0&loginSid=0&appUid=76039576",
                "https://www.kuwo.cn/");
            if (root.Value<int?>("code") != 200) throw new InvalidOperationException("酷我歌单分类读取失败");
            var collections = new List<OnlineCollection>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in (root["data"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var groupName = TextUtil.HtmlDecode(group.Value<string>("name"));
                foreach (var item in (group["data"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var id = item.Value<string>("id") ?? string.Empty;
                    var digest = item.Value<string>("digest") ?? string.Empty;
                    var title = TextUtil.HtmlDecode(item.Value<string>("name"));
                    var categoryId = id + "-" + digest;
                    if (digest != "10000" || id.Length == 0 || title.Length == 0 || !seen.Add(categoryId)) continue;
                    collections.Add(new OnlineCollection
                    {
                        Platform = OnlinePlatform.Kuwo,
                        Id = categoryId,
                        Title = title,
                        Subtitle = groupName,
                        ArtworkUrl = Https(item.Value<string>("img")),
                        Kind = OnlineCollectionKind.Category
                    });
                }
            }
            return SliceCollections(collections, page, limit);
        }

        private static OnlineCollectionPage QqPlaylists(string categoryId, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var begin = (safePage - 1) * safeLimit;
            var root = GetJson(OnlineHttp.Query("https://c.y.qq.com/splcloud/fcgi-bin/fcg_get_diss_by_tag.fcg",
                new Dictionary<string, object>
                {
                    ["picmid"] = 1, ["g_tk"] = 5381, ["loginUin"] = 0, ["hostUin"] = 0,
                    ["format"] = "json", ["inCharset"] = "utf8", ["outCharset"] = "utf-8",
                    ["notice"] = 0, ["platform"] = "yqq.json", ["needNewCode"] = 0,
                    ["categoryId"] = categoryId, ["sortId"] = 5, ["sin"] = begin,
                    ["ein"] = begin + safeLimit - 1
                }), "https://y.qq.com/");
            if (root.Value<int?>("code") != 0) throw new InvalidOperationException("QQ音乐精选歌单读取失败");
            var data = root["data"] as JObject ?? new JObject();
            var list = data["list"] as JArray ?? new JArray();
            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>("dissid") ?? string.Empty;
                var title = TextUtil.HtmlDecode(item.Value<string>("dissname"));
                if (id.Length == 0 || title.Length == 0) continue;
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Qq,
                    Id = id,
                    Title = title,
                    Subtitle = (item["creator"] as JObject)?.Value<string>("name") ?? string.Empty,
                    ArtworkUrl = Https(item.Value<string>("imgurl")),
                    Kind = OnlineCollectionKind.Playlist
                });
            }
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(safePage, safeLimit, data.Value<int?>("sum"), list.Count);
        }

        private static OnlineCollectionPage NeteasePlaylists(string category, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var root = GetJson(OnlineHttp.Query("https://music.163.com/api/playlist/list",
                new Dictionary<string, object>
                {
                    ["cat"] = category, ["order"] = "hot", ["offset"] = (safePage - 1) * safeLimit,
                    ["total"] = true, ["limit"] = safeLimit
                }), "https://music.163.com/");
            if (root.Value<int?>("code") != 200) throw new InvalidOperationException("网易云精选歌单读取失败");
            var list = root["playlists"] as JArray ?? new JArray();
            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>("id") ?? string.Empty;
                var title = (item.Value<string>("name") ?? string.Empty).Trim();
                if (id.Length == 0 || title.Length == 0) continue;
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Netease,
                    Id = id,
                    Title = title,
                    Subtitle = (item["creator"] as JObject)?.Value<string>("nickname") ?? string.Empty,
                    ArtworkUrl = Https(item.Value<string>("coverImgUrl")),
                    Kind = OnlineCollectionKind.Playlist,
                    TrackCount = item.Value<int?>("trackCount") ?? 0
                });
            }
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(safePage, safeLimit, root.Value<int?>("total"), list.Count);
        }

        private static OnlineCollectionPage KugouPlaylists(int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var root = GetJson(OnlineHttp.Query("https://m.kugou.com/plist/index",
                new Dictionary<string, object> { ["json"] = true, ["page"] = safePage }),
                "https://m.kugou.com/");
            var data = root.SelectToken("plist.list") as JObject;
            if (data == null) throw new InvalidOperationException("酷狗精选歌单读取失败");
            var list = data["info"] as JArray ?? new JArray();
            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>("specialid") ?? string.Empty;
                var title = TextUtil.HtmlDecode(item.Value<string>("specialname"));
                if (id.Length == 0 || title.Length == 0) continue;
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Kugou,
                    Id = id,
                    Title = title,
                    Subtitle = item.Value<long?>("playcount") is long plays && plays > 0
                        ? plays.ToString("N0", CultureInfo.InvariantCulture) + " 次播放"
                        : "酷狗精选歌单",
                    ArtworkUrl = Https(item.Value<string>("imgurl")).Replace("{size}", "400"),
                    Kind = OnlineCollectionKind.Playlist,
                    TrackCount = item.Value<int?>("songcount") ?? 0
                });
            }
            var total = data.Value<int?>("total");
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(safePage, 30, total, list.Count);
        }

        private static OnlineCollectionPage KuwoPlaylists(string categoryId, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            string url;
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                url = OnlineHttp.Query("https://wapi.kuwo.cn/api/pc/classify/playlist/getRcmPlayList",
                    new Dictionary<string, object>
                    {
                        ["loginUid"] = 0, ["loginSid"] = 0, ["appUid"] = 76039576,
                        ["pn"] = safePage, ["rn"] = safeLimit, ["order"] = "hot"
                    });
            }
            else
            {
                var parts = categoryId.Split('-');
                if (parts.Length != 2) throw new InvalidOperationException("酷我歌单分类标识无效");
                url = OnlineHttp.Query("https://wapi.kuwo.cn/api/pc/classify/playlist/getTagPlayList",
                    new Dictionary<string, object>
                    {
                        ["loginUid"] = 0, ["loginSid"] = 0, ["appUid"] = 76039576,
                        ["pn"] = safePage, ["id"] = parts[0], ["rn"] = safeLimit
                    });
            }
            var root = GetJson(url, "https://www.kuwo.cn/");
            if (root.Value<int?>("code") != 200) throw new InvalidOperationException("酷我精选歌单读取失败");
            var data = root["data"] as JObject ?? new JObject();
            var list = data["data"] as JArray ?? new JArray();
            var collections = new List<OnlineCollection>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = item.Value<string>("id") ?? string.Empty;
                var title = TextUtil.HtmlDecode(item.Value<string>("name"));
                if (id.Length == 0 || title.Length == 0) continue;
                collections.Add(new OnlineCollection
                {
                    Platform = OnlinePlatform.Kuwo,
                    Id = id,
                    Title = title,
                    Subtitle = item.Value<string>("uname") ?? string.Empty,
                    ArtworkUrl = Https(item.Value<string>("img")),
                    Kind = OnlineCollectionKind.Playlist,
                    TrackCount = item.Value<int?>("total") ?? 0
                });
            }
            return new OnlineCollectionPage { Collections = collections }
                .WithPagination(safePage, safeLimit, data.Value<int?>("total"), list.Count);
        }

        private static OnlineSearchPage QqRankingTracks(OnlineCollection ranking, int page)
        {
            var safePage = Math.Max(1, page);
            const int limit = 100;
            var root = GetJson(OnlineHttp.Query("https://c.y.qq.com/v8/fcg-bin/fcg_v8_toplist_cp.fcg",
                new Dictionary<string, object>
                {
                    ["topid"] = ranking.Id, ["format"] = "json", ["inCharset"] = "utf8",
                    ["outCharset"] = "utf-8", ["notice"] = 0, ["platform"] = "yqq.json",
                    ["needNewCode"] = 0, ["song_begin"] = (safePage - 1) * limit, ["song_num"] = limit
                }), "https://y.qq.com/");
            if (root.Value<int?>("code") != 0) throw new InvalidOperationException("QQ音乐榜单歌曲读取失败");
            var raw = root["songlist"] as JArray ?? new JArray();
            var tracks = raw.OfType<JObject>()
                .Select(item => OnlineMusicRepository.ParseQqTrack(item["data"] as JObject ?? item))
                .Where(track => track != null).ToList();
            var total = root.Value<int?>("total_song_num") ?? tracks.Count;
            return new OnlineSearchPage
            {
                Tracks = tracks,
                Page = safePage,
                Total = total,
                TotalKnown = total > 0,
                HasNext = (long)safePage * limit < total
            };
        }

        private static OnlineSearchPage KugouRankingTracks(OnlineCollection ranking, int page)
        {
            var safePage = Math.Max(1, page);
            const int limit = 100;
            var parts = (ranking.Id ?? string.Empty).Split(':');
            if (parts.Length != 2) throw new InvalidOperationException("酷狗榜单标识无效");
            var root = GetJson(OnlineHttp.Query("https://mobiles.kugou.com/api/v3/rank/song",
                new Dictionary<string, object>
                {
                    ["version"] = 9108, ["plat"] = 0, ["area_code"] = 1,
                    ["rankid"] = parts[0], ["rank_cid"] = parts[1],
                    ["page"] = safePage, ["pagesize"] = limit
                }), "https://www.kugou.com/");
            if (root.Value<int?>("errcode") != 0) throw new InvalidOperationException("酷狗榜单歌曲读取失败");
            var data = root["data"] as JObject ?? new JObject();
            var list = data["info"] as JArray ?? new JArray();
            var tracks = list.OfType<JObject>().Select(OnlineMusicRepository.ParseKugouTrack)
                .Where(track => track != null).ToList();
            var total = data.Value<int?>("total") ?? tracks.Count;
            return new OnlineSearchPage
            {
                Tracks = tracks,
                Page = safePage,
                Total = total,
                TotalKnown = total > 0,
                HasNext = (long)safePage * limit < total
            };
        }

        private static OnlineSearchPage KuwoRankingTracks(OnlineCollection ranking, int page)
        {
            var safePage = Math.Max(1, page);
            const int limit = 100;
            var root = GetJson(OnlineHttp.Query("https://kbangserver.kuwo.cn/ksong.s",
                new Dictionary<string, object>
                {
                    ["from"] = "pc", ["fmt"] = "json", ["pn"] = safePage - 1, ["rn"] = limit,
                    ["type"] = "bang", ["data"] = "content", ["id"] = ranking.Id,
                    ["show_copyright_off"] = 0, ["pcmp4"] = 1, ["isbang"] = 1
                }), "https://www.kuwo.cn/");
            var tracks = (root["musiclist"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(KuwoTrack).Where(track => track != null).ToList();
            var total = root.Value<int?>("num") ?? tracks.Count;
            return new OnlineSearchPage
            {
                Tracks = tracks, Page = safePage, Total = total, TotalKnown = total > 0,
                HasNext = (long)safePage * limit < total
            };
        }

        private static OnlineSearchPage MiguRankingTracks(OnlineCollection ranking, int page)
        {
            var safePage = Math.Max(1, page);
            const int pageSize = 100;
            var root = GetJson(OnlineHttp.Query("https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/querycontentbyId.do",
                new Dictionary<string, object> { ["columnId"] = ranking.Id, ["needAll"] = 0 }),
                "https://app.c.nf.migu.cn/");
            if ((root.Value<string>("code") ?? string.Empty) != "000000")
                throw new InvalidOperationException("咪咕榜单读取失败");
            var raw = root.SelectToken("columnInfo.contents") as JArray ?? new JArray();
            var start = (safePage - 1) * pageSize;
            var tracks = raw.Skip(start).Take(pageSize).OfType<JObject>()
                .Select(item => MiguTrack(item["objectInfo"] as JObject))
                .Where(track => track != null).ToList();
            var total = raw.Count;
            return new OnlineSearchPage
            {
                Tracks = tracks, Page = safePage, Total = total, TotalKnown = total > 0,
                HasNext = (long)safePage * pageSize < total
            };
        }

        private static OnlineTrack KuwoTrack(JObject item)
        {
            if (item == null) return null;
            var id = (item.Value<string>("id") ?? item.Value<string>("MUSICRID") ?? string.Empty)
                .Replace("MUSIC_", string.Empty);
            var title = TextUtil.HtmlDecode(item.Value<string>("name") ?? item.Value<string>("SONGNAME"));
            if (id.Length == 0 || title.Length == 0) return null;
            var artist = TextUtil.HtmlDecode(item.Value<string>("artist") ?? item.Value<string>("ARTIST"));
            var album = TextUtil.HtmlDecode(item.Value<string>("album") ?? item.Value<string>("ALBUM"));
            var albumId = item.Value<string>("albumid") ?? item.Value<string>("ALBUMID") ?? string.Empty;
            var seconds = item.Value<long?>("song_duration") ?? item.Value<long?>("duration") ??
                item.Value<long?>("DURATION") ?? 0;
            if (seconds > 10000) seconds /= 1000;
            var duration = seconds * 1000;
            var image = Https(item.Value<string>("albumpic") ?? item.Value<string>("pic") ??
                item.Value<string>("web_albumpic_short"));
            var formats = string.Join("|", new[]
            {
                item.Value<string>("formats"), item.Value<string>("N_MINFO"),
                item.Value<string>("MINFO")
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var qualities = new List<OnlineQuality>();
            if (formats.IndexOf("ALFLAC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                formats.IndexOf("bitrate:2000", StringComparison.OrdinalIgnoreCase) >= 0)
                qualities.Add(OnlineQuality.Lossless);
            if (formats.IndexOf("MP3H", StringComparison.OrdinalIgnoreCase) >= 0 ||
                formats.IndexOf("bitrate:320", StringComparison.OrdinalIgnoreCase) >= 0)
                qualities.Add(OnlineQuality.High);
            qualities.Add(OnlineQuality.Standard);
            var types = new JObject();
            foreach (var quality in qualities.Distinct())
                types[quality.Id] = new JObject();
            var info = new JObject
            {
                ["source"] = "kw", ["id"] = id, ["songId"] = id, ["songmid"] = id,
                ["name"] = title, ["title"] = title, ["singer"] = artist, ["artist"] = artist,
                ["albumName"] = album, ["albumId"] = albumId, ["img"] = image,
                ["interval"] = duration / 60000 + ":" + duration / 1000 % 60,
                ["_types"] = types, ["types"] = new JArray(types.Properties().Select(p => new JObject { ["type"] = p.Name }))
            };
            return new OnlineTrack
            {
                Platform = OnlinePlatform.Kuwo, Id = id, Title = title, Artist = artist,
                Album = album, DurationMs = duration, ArtworkUrl = image,
                AvailableQualities = qualities.Distinct().ToList(),
                MusicInfoJson = info.ToString(Formatting.None)
            };
        }

        private static OnlineTrack MiguTrack(JObject item)
        {
            if (item == null) return null;
            var id = item.Value<string>("songId") ?? item.Value<string>("contentId") ?? string.Empty;
            var title = TextUtil.HtmlDecode(item.Value<string>("songName") ?? item.Value<string>("name"));
            if (id.Length == 0 || title.Length == 0) return null;
            var artist = TextUtil.HtmlDecode(item.Value<string>("singer"));
            var album = TextUtil.HtmlDecode(item.Value<string>("album"));
            var copyright = item.Value<string>("copyrightId") ?? string.Empty;
            var cover = (item["albumImgs"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(image => image.Value<string>("img")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            var length = item.Value<string>("length") ?? string.Empty;
            TimeSpan duration;
            var milliseconds = TimeSpan.TryParse(length, out duration) ? (long)duration.TotalMilliseconds : 0L;
            var formats = item["newRateFormats"] as JArray ?? item["rateFormats"] as JArray ?? new JArray();
            var qualities = new List<OnlineQuality>();
            foreach (var format in formats.OfType<JObject>())
            {
                OnlineQuality quality = null;
                switch (format.Value<string>("formatType"))
                {
                    case "PQ": quality = OnlineQuality.Standard; break;
                    case "HQ": quality = OnlineQuality.High; break;
                    case "SQ": quality = OnlineQuality.Lossless; break;
                    case "ZQ": case "ZQ24": quality = OnlineQuality.HiRes; break;
                }
                if (quality != null && !qualities.Contains(quality)) qualities.Add(quality);
            }
            if (qualities.Count == 0) qualities.Add(OnlineQuality.Standard);
            var types = new JObject();
            foreach (var quality in qualities)
                types[quality == OnlineQuality.HiRes ? "flac24bit" : quality.Id] = new JObject();
            var info = new JObject
            {
                ["source"] = "mg", ["id"] = id,
                ["songId"] = id, ["songmid"] = id, ["copyrightId"] = copyright,
                ["name"] = title, ["title"] = title, ["singer"] = artist, ["artist"] = artist,
                ["albumName"] = album, ["albumId"] = item.Value<string>("albumId") ?? "",
                ["img"] = Https(cover), ["lrcUrl"] = item.Value<string>("lrcUrl") ?? "",
                ["mrcUrl"] = item.Value<string>("mrcUrl") ?? "", ["trcUrl"] = item.Value<string>("trcUrl") ?? "",
                ["interval"] = length,
                ["_types"] = types, ["types"] = new JArray(types.Properties().Select(p => new JObject { ["type"] = p.Name }))
            };
            return new OnlineTrack
            {
                Platform = OnlinePlatform.Migu, Id = id, Title = title, Artist = artist,
                Album = album, DurationMs = milliseconds, ArtworkUrl = Https(cover),
                AvailableQualities = qualities, MusicInfoJson = info.ToString(Formatting.None)
            };
        }

        private static OnlineCollectionPage SliceCollections(
            List<OnlineCollection> all, int page, int limit)
        {
            page = Math.Max(1, page);
            limit = Math.Max(1, Math.Min(50, limit));
            var start = (long)(page - 1) * limit;
            var values = start >= all.Count
                ? new List<OnlineCollection>()
                : all.Skip((int)start).Take(limit).ToList();
            return new OnlineCollectionPage { Collections = values }
                .WithPagination(page, limit, all.Count, values.Count);
        }

        private static JObject GetJson(string url, string referer)
        {
            var response = OnlineHttp.Execute(url, headers: new Dictionary<string, string>
            {
                ["User-Agent"] = OnlineHttp.BrowserUserAgent,
                ["Referer"] = referer
            }, maxBytes: 8 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException("在线服务返回 HTTP " + response.StatusCode);
            if (string.IsNullOrWhiteSpace(response.Body)) throw new InvalidOperationException("在线服务返回空响应");
            return JObject.Parse(response.Body.TrimStart('\uFEFF'));
        }

        private static string Https(string value) =>
            (value ?? string.Empty).Trim().Replace("http://", "https://");
    }
}
