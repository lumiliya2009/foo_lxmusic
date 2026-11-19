using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    // Public catalog queries only; playback URLs still come from the selected
    // LX source. Protocol references are listed in THIRD_PARTY_NOTICES.md.
    public static class ExtendedCatalog
    {
        public static OnlineSearchPage Search(OnlinePlatform platform, string query, int page, int limit)
        {
            page = Math.Max(1, page); limit = Math.Max(1, Math.Min(50, limit));
            if (platform == OnlinePlatform.Kuwo)
            {
                var url = OnlineHttp.Query("https://search.kuwo.cn/r.s", new Dictionary<string, object>
                {
                    ["client"] = "kt", ["all"] = query, ["pn"] = page - 1, ["rn"] = limit,
                    ["ver"] = "kwplayer_ar_9.2.2.1", ["vipver"] = 1, ["show_copyright_off"] = 1,
                    ["newver"] = 1, ["ft"] = "music", ["cluster"] = 0, ["strategy"] = 2012,
                    ["encoding"] = "utf8", ["rformat"] = "json", ["vermerge"] = 1, ["mobi"] = 1
                });
                return ParseKuwo(Read(url), page, limit);
            }
            if (platform == OnlinePlatform.Migu)
            {
                var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                const string device = "963B7AA0D21511ED807EE5846EC87D20";
                var sign = TextUtil.Md5Hex(query + "6cdc72a439cef99a3418d2a78aa28c73" +
                    "yyapp2d16148780a1dcc7408e06336b98cfd50" + device + time);
                var url = OnlineHttp.Query("https://jadeite.migu.cn/music_search/v3/search/searchAll", new Dictionary<string, object>
                {
                    ["isCorrect"] = 0, ["isCopyright"] = 1, ["searchSwitch"] = "{\"song\":1}",
                    ["pageSize"] = limit, ["text"] = query, ["pageNo"] = page, ["sort"] = 0, ["sid"] = "USS"
                });
                return ParseMigu(Read(url, new Dictionary<string, string>
                {
                    ["uiVersion"] = "A_music_3.6.1", ["deviceId"] = device, ["timestamp"] = time,
                    ["sign"] = sign, ["channel"] = "0146921",
                    ["User-Agent"] = "Mozilla/5.0 (Linux; Android 11) AppleWebKit/534.30 Mobile Safari/534.30"
                }), page, limit);
            }
            throw new InvalidOperationException(platform.Label + "需要音源提供搜索接口，当前音源只提供播放解析");
        }

        private static string Read(string url, Dictionary<string, string> headers = null)
        {
            var response = OnlineHttp.Execute(url, headers: headers, maxBytes: 8 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException("站点搜索失败（HTTP " + response.StatusCode + "）");
            if (string.IsNullOrWhiteSpace(response.Body)) throw new InvalidOperationException("站点搜索返回空响应，请稍后重试");
            return response.Body;
        }

        public static OnlineSearchPage ParseKuwo(string json, int page, int limit)
        {
            var root = JObject.Parse(json);
            if (!(root["abslist"] is JArray list)) throw new InvalidOperationException("酷我搜索返回的数据格式不正确");
            var tracks = new List<OnlineTrack>();
            foreach (var item in list.OfType<JObject>())
            {
                var id = (item.Value<string>("MUSICRID") ?? "").Replace("MUSIC_", "");
                var title = TextUtil.HtmlDecode(item.Value<string>("SONGNAME") ?? item.Value<string>("NAME"));
                if (id.Length == 0 || string.IsNullOrWhiteSpace(title)) continue;
                var qualities = new List<OnlineQuality>();
                foreach (Match match in Regex.Matches(item.Value<string>("N_MINFO") ?? item.Value<string>("MINFO") ?? "", @"bitrate:(\d+),format:(\w+)"))
                {
                    OnlineQuality q = null;
                    switch (match.Groups[1].Value)
                    {
                        case "128": q = OnlineQuality.Standard; break;
                        case "320": q = OnlineQuality.High; break;
                        case "2000": q = OnlineQuality.Lossless; break;
                        case "4000": q = OnlineQuality.HiRes; break;
                    }
                    if (q != null && !qualities.Contains(q)) qualities.Add(q);
                }
                var image = item.Value<string>("web_albumpic_short") ?? "";
                if (image.Length > 0 && !image.StartsWith("http")) image = "https://img4.kuwo.cn/star/albumcover/" + image;
                tracks.Add(Track(OnlinePlatform.Kuwo, id, title, TextUtil.HtmlDecode(item.Value<string>("ARTIST")),
                    TextUtil.HtmlDecode(item.Value<string>("ALBUM")), item.Value<string>("ALBUMID"),
                    (item.Value<long?>("DURATION") ?? 0) * 1000, image, qualities));
            }
            return Page(tracks, page, limit, root.Value<int?>("TOTAL") ?? tracks.Count);
        }

        public static OnlineSearchPage ParseMigu(string json, int page, int limit)
        {
            var root = JObject.Parse(json);
            if (root.Value<string>("code") != "000000") throw new InvalidOperationException("咪咕搜索失败：" + (root.Value<string>("info") ?? "接口暂不可用"));
            var result = root["songResultData"] as JObject ?? new JObject();
            var list = result["resultList"] as JArray ?? new JArray();
            var tracks = new List<OnlineTrack>();
            var seen = new HashSet<string>();
            foreach (var item in list.SelectMany(entry => entry is JArray nested ? nested.OfType<JObject>() : new[] { entry as JObject }).Where(item => item != null))
            {
                var id = item.Value<string>("songId") ?? "";
                var copyright = item.Value<string>("copyrightId") ?? "";
                var title = (item.Value<string>("name") ?? "").Trim();
                if (id.Length == 0 || title.Length == 0 || !seen.Add(copyright.Length > 0 ? copyright : id)) continue;
                var qualities = new List<OnlineQuality>();
                foreach (var format in (item["audioFormats"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    OnlineQuality q = null;
                    switch (format.Value<string>("formatType"))
                    {
                        case "PQ": q = OnlineQuality.Standard; break;
                        case "HQ": q = OnlineQuality.High; break;
                        case "SQ": q = OnlineQuality.Lossless; break;
                        case "ZQ": case "ZQ24": q = OnlineQuality.HiRes; break;
                    }
                    if (q != null && !qualities.Contains(q)) qualities.Add(q);
                }
                var image = item.Value<string>("img3") ?? item.Value<string>("img2") ?? item.Value<string>("img1") ?? "";
                if (image.StartsWith("/")) image = "https://d.musicapp.migu.cn" + image;
                var artist = string.Join("、", (item["singerList"] as JArray ?? new JArray()).OfType<JObject>()
                    .Select(singer => singer.Value<string>("name")).Where(name => !string.IsNullOrWhiteSpace(name)));
                var track = Track(OnlinePlatform.Migu, id, title, artist, item.Value<string>("album"), item.Value<string>("albumId"),
                    (item.Value<long?>("duration") ?? 0) * 1000, image, qualities);
                var info = JObject.Parse(track.MusicInfoJson);
                info["copyrightId"] = copyright;
                info["lrcUrl"] = item.Value<string>("lrcUrl") ?? item.Value<string>("lyricUrl") ?? "";
                track.MusicInfoJson = info.ToString(Formatting.None);
                tracks.Add(track);
            }
            return Page(tracks, page, limit, result.Value<int?>("totalCount") ?? tracks.Count);
        }

        private static OnlineTrack Track(OnlinePlatform platform, string id, string title, string artist, string album,
            string albumId, long duration, string image, List<OnlineQuality> qualities)
        {
            var types = new JObject();
            foreach (var quality in qualities) types[quality == OnlineQuality.HiRes ? "flac24bit" : quality.Id] = new JObject();
            var info = new JObject
            {
                ["source"] = platform.LxSourceId, ["id"] = id, ["songId"] = id, ["songmid"] = id,
                ["name"] = title, ["title"] = title, ["singer"] = artist ?? "", ["artist"] = artist ?? "",
                ["albumName"] = album ?? "", ["albumId"] = albumId ?? "", ["img"] = image,
                ["interval"] = (duration / 60000).ToString("00") + ":" + (duration / 1000 % 60).ToString("00"),
                ["_types"] = types, ["types"] = new JArray(types.Properties().Select(p => new JObject { ["type"] = p.Name }))
            };
            return new OnlineTrack { Platform = platform, Id = id, Title = title, Artist = artist ?? "", Album = album ?? "",
                DurationMs = Math.Max(0, duration), ArtworkUrl = image, AvailableQualities = qualities, MusicInfoJson = info.ToString(Formatting.None) };
        }

        private static OnlineSearchPage Page(List<OnlineTrack> tracks, int page, int limit, int total) =>
            new OnlineSearchPage { Tracks = tracks, Page = page, Total = total, HasNext = (long)page * limit < total };
    }
}
