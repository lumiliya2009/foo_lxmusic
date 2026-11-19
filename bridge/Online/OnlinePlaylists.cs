﻿﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TzPlayer.Win.Online
{
    public sealed class OnlinePlaylistTarget
    {
        public OnlinePlatform Platform { get; set; }
        public string Id { get; set; }
    }

    /// <summary>从分享文本中解析平台歌单标识（不联网）。</summary>
    public static class OnlinePlaylistInputParser
    {
        private static readonly System.Text.RegularExpressions.Regex UrlPattern =
            new System.Text.RegularExpressions.Regex("https?://[^\\s<>\"']+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string QqNumber(string input)
        {
            var compact = System.Text.RegularExpressions.Regex.Replace((input ?? string.Empty).Trim(), "\\s+", string.Empty);
            if (compact.Length == 0) return null;
            var match = System.Text.RegularExpressions.Regex.Match(compact, "(?i)^(?:qq号|qq)[:：-]?(\\d{5,12})$");
            var candidate = match.Success ? match.Groups[1].Value : compact;
            if (!System.Text.RegularExpressions.Regex.IsMatch(candidate, "^\\d{5,12}$")) return null;
            return candidate;
        }

        public static List<string> ExtractUrls(string input)
        {
            var result = new List<string>();
            foreach (System.Text.RegularExpressions.Match match in UrlPattern.Matches(input ?? string.Empty))
            {
                var url = TrimUrl(match.Value);
                if (!string.IsNullOrWhiteSpace(url) && !result.Contains(url)) result.Add(url);
            }
            return result;
        }

        public static OnlinePlaylistTarget ParseUrl(string rawUrl)
        {
            var url = DecodePercentEscapes(TrimUrl(rawUrl));
            if (string.IsNullOrWhiteSpace(url)) return null;
            var lower = url.ToLowerInvariant();

            OnlinePlatform platform;
            if (HostMatches(url, h => h.Contains("qq.com"))) platform = OnlinePlatform.Qq;
            else if (HostMatches(url, h => h.Contains("kugou.com"))) platform = OnlinePlatform.Kugou;
            else if (HostMatches(url, h => h == "163cn.tv" || h.EndsWith(".163cn.tv") || h.Contains("163.com"))) platform = OnlinePlatform.Netease;
            else return null;

            string id = null;
            if (ReferenceEquals(platform, OnlinePlatform.Qq))
            {
                id = FirstMatch(url, "/(?:playlist|taoge|playsquare)/(\\d+)", "(?:^|[?&#])(?:disstid|playlistid)=(\\d+)");
                if (id == null && (lower.Contains("/playlist") || lower.Contains("/taoge") || lower.Contains("/share/details")))
                    id = FirstMatch(url, "(?:^|[?&#])id=(\\d+)");
            }
            else if (ReferenceEquals(platform, OnlinePlatform.Kugou))
            {
                id = FirstMatch(url,
                    "/(?:songlist|playlist)/(gcid_[A-Za-z0-9_-]+)",
                    "/(?:special/single|songlist|playlist)/(collection_[A-Za-z0-9_-]+)",
                    "(?:^|[?&#])(?:global_collection_id|global_specialid|collection_id)=(collection_[A-Za-z0-9_-]+)",
                    // 数字标识必须紧跟 .html / ? # / 或结尾，避免把字母数字混排短链截成开头几位数字。
                    "/(?:special/single|songlist|playlist)/(\\d+)(?![A-Za-z0-9_-])",
                    "(?:^|[?&#])(?:specialid|global_collection_id|listid)=(\\d+)(?![A-Za-z0-9_-])");
                if (id == null && (lower.Contains("/songlist") || lower.Contains("/special/single") || lower.Contains("/playlist")))
                    id = FirstMatch(url, "(?:^|[?&#])id=(\\d+)(?![A-Za-z0-9_-])");
                // 新版分享短链常用字母数字混排标识（例如 /songlist/7b5c3e3e...）。
                if (id == null && lower.Contains("/songlist"))
                    id = FirstMatch(url, "/(?:songlist|playlist)/([A-Za-z0-9_-]{10,})");
            }
            else
            {
                id = FirstMatch(url, "/(?:m/)?playlist/(\\d+)");
                if (id == null && (lower.Contains("/playlist") || lower.Contains("#/playlist")))
                    id = FirstMatch(url, "(?:^|[?&#])id=(\\d+)");
            }

            return string.IsNullOrWhiteSpace(id) ? null : new OnlinePlaylistTarget { Platform = platform, Id = id };
        }

        private static bool HostMatches(string url, Func<string, bool> predicate)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, "https?://([^/?#]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            var host = match.Groups[1].Value;
            var at = host.LastIndexOf('@');
            if (at >= 0) host = host.Substring(at + 1);
            var colon = host.IndexOf(':');
            if (colon >= 0) host = host.Substring(0, colon);
            return predicate(host.ToLowerInvariant());
        }

        private static string FirstMatch(string value, params string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(value, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value;
            }
            return null;
        }

        private static string TrimUrl(string value)
        {
            var cut = value.Split(new[] { '，', '。', '；', '：', '！', '？', '）', '】', '》' })[0].Trim();
            return cut.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '，', '。', '；', '：', '！', '？', '）', '】', '》', '"', '\'');
        }

        private static string DecodePercentEscapes(string value)
        {
            var current = value;
            for (var i = 0; i < 2; i++)
            {
                string decoded;
                try { decoded = Uri.UnescapeDataString(current.Replace("+", "%2B")); }
                catch { return current; }
                if (decoded == current) return current;
                current = decoded;
            }
            return current;
        }
    }

    /// <summary>
    /// 酷狗公开歌单接入：数字 specialid、新版 collection_ 分享标识、gcid_ 短链分享标识全部支持。
    /// 数字标识优先走 v3 接口（无需签名、http 兜底），collection_ / gcid_ 走 v5 签名接口。
    /// </summary>
    internal static class KugouPlaylistApi
    {
        private const int PageSize = 100;
        private const string SignatureSalt = "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt";
        private const string AndroidSignatureSalt = "OIlwieks28dk2k092lksi2UIkp";
        private const string InfoUrl = "https://mobiles.kugou.com/api/v5/special/info_v2";
        private const string SongsV5Url = "https://mobiles.kugou.com/api/v5/special/song_v2";
        private const string InfoV3Http = "http://mobilecdnbj.kugou.com/api/v3/special/info";
        private const string InfoV3Https = "https://mobilecdnbj.kugou.com/api/v3/special/info";
        private const string SongsV3Http = "http://mobilecdnbj.kugou.com/api/v3/special/song";
        private const string SongsV3Https = "https://mobilecdnbj.kugou.com/api/v3/special/song";
        private const string DecodeUrl = "https://t.kugou.com/v1/songlist/batch_decode";
        private const string DecodeParameters = "dfid=-&appid=1005&mid=0&clientver=20109&clienttime=640612895&uuid=-";
        private const string MobileUserAgent =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 11_0 like Mac OS X) AppleWebKit/604.1.38 Version/11.0 Mobile/15A372 Safari/604.1";
        private const string ShareReferer = "https://m3ws.kugou.com/share/index.php";

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, string> GlobalIdCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static OnlineCollection Metadata(string id)
        {
            var resolved = ResolveId(id);
            JObject info = null;
            if (IsNumeric(resolved)) info = TryV3Info(resolved) ?? TryRequest(InfoUrl, InfoParameters(resolved));
            else if (IsCollectionId(resolved)) info = TryRequest(InfoUrl, InfoParameters(resolved));
            if (info != null && !string.IsNullOrWhiteSpace(Clean(info.Value<string>("specialname"))))
                return CollectionFromInfo(id, info);

            var fallback = new OnlineCollection
            {
                Platform = OnlinePlatform.Kugou,
                Id = id,
                Title = "酷狗歌单 " + id,
                Kind = OnlineCollectionKind.Playlist
            };
            try
            {
                fallback.TrackCount = Tracks(fallback, 1).Total;
            }
            catch
            {
            }
            return fallback;
        }

        public static OnlineSearchPage Tracks(OnlineCollection collection, int page)
        {
            var safePage = Math.Max(1, page);
            var raw = collection == null ? string.Empty : collection.Id ?? string.Empty;
            var id = ResolveId(raw);

            if (IsNumeric(id))
            {
                var v3 = TryV3Songs("specialid=" + Uri.EscapeDataString(id), collection, safePage);
                if (v3 != null) return v3;
                var v5 = TryRequest(SongsV5Url, SongParameters(id, safePage));
                if (v5 != null)
                {
                    var built = BuildSongPage(v5, collection, safePage);
                    if (built.Tracks.Count > 0 || built.Total > 0 || safePage > 1) return built;
                }
            }
            else if (IsCollectionId(id))
            {
                var v5 = TryRequest(SongsV5Url, SongParameters(id, safePage));
                if (v5 != null)
                {
                    var built = BuildSongPage(v5, collection, safePage);
                    if (built.Tracks.Count > 0 || built.Total > 0 || safePage > 1) return built;
                }
                var v3 = TryV3Songs("specialid=0&global_specialid=" + Uri.EscapeDataString(id), collection, safePage);
                if (v3 != null) return v3;
            }

            if (IsGcid(raw))
                throw new InvalidOperationException("酷狗没有识别出这个分享歌单（" + raw + "），可能已删除、设为私密或分享已过期。");
            throw new InvalidOperationException("酷狗音乐没有返回这个歌单的歌曲，请确认歌单已公开且链接完整。");
        }

        /// <summary>把 gcid_ 分享标识换算成 collection_ 全局歌单标识；其它标识原样返回。</summary>
        private static string ResolveId(string id)
        {
            var value = (id ?? string.Empty).Trim();
            if (!IsGcid(value)) return value;
            lock (CacheSync)
            {
                string cached;
                if (GlobalIdCache.TryGetValue(value, out cached)) return cached;
            }
            var resolved = TryDecodeViaBatchApi(value);
            if (string.IsNullOrWhiteSpace(resolved)) resolved = TryDecodeViaSharePage(value);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                lock (CacheSync) GlobalIdCache[value] = resolved;
                return resolved;
            }
            return value;
        }

        /// <summary>酷狗 t.kugou.com 的批量解码接口（android 签名）。</summary>
        private static string TryDecodeViaBatchApi(string gcid)
        {
            try
            {
                var body = "{\"ret_info\":1,\"data\":[{\"id\":\"" + gcid + "\",\"id_type\":2}]}";
                var signature = AndroidSignature(DecodeParameters, body);
                var url = DecodeUrl + "?" + DecodeParameters + "&signature=" + signature;
                var response = OnlineHttp.Execute(url, "POST", new Dictionary<string, string>
                {
                    ["Referer"] = "https://m.kugou.com/",
                    ["User-Agent"] = "Mozilla/5.0 (Linux; Android 10; HUAWEI HMA-AL00) AppleWebKit/537.36 Chrome/83.0.4103.106 Mobile Safari/537.36",
                    ["Content-Type"] = "application/json"
                }, Encoding.UTF8.GetBytes(body), maxBytes: 1024 * 1024);
                if (!response.Ok) return null;
                var root = OnlineMusicRepository.SafeParse(response.Body);
                if (root == null) return null;
                var code = root.Value<int?>("err_code") ?? root.Value<int?>("errcode") ?? -1;
                if (code != 0) return null;
                var first = (root.SelectToken("data.list") as JArray)?.OfType<JObject>().FirstOrDefault();
                if (first == null) return null;
                var globalId = first.Value<string>("global_collection_id");
                if (string.IsNullOrWhiteSpace(globalId)) globalId = first.Value<string>("gid");
                if (string.IsNullOrWhiteSpace(globalId)) globalId = first.Value<string>("specialid");
                return string.IsNullOrWhiteSpace(globalId) ? null : globalId.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>兜底：读取酷狗分享页 HTML 里的 window.$output 数据块。</summary>
        private static string TryDecodeViaSharePage(string gcid)
        {
            try
            {
                var response = OnlineHttp.Execute("https://m.kugou.com/songlist/" + Uri.EscapeDataString(gcid) + "/",
                    headers: new Dictionary<string, string>
                    {
                        ["Referer"] = ShareReferer,
                        ["User-Agent"] = "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 Chrome/120 Mobile Safari/537.36"
                    }, maxBytes: 1024 * 1024);
                if (!response.Ok) return null;
                var html = response.Body ?? string.Empty;
                var json = ExtractOutputJson(html);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var root = OnlineMusicRepository.SafeParse(json);
                    foreach (var path in new[] { "info.listinfo.global_collection_id", "info.listinfo.global_specialid", "info.global_collection_id" })
                    {
                        var value = root?.SelectToken(path)?.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
                var match = System.Text.RegularExpressions.Regex.Match(html, "collection_[0-9A-Za-z_]{6,}");
                return match.Success ? match.Value : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从 window.$output = {...} 里截出平衡的 JSON 对象文本。</summary>
        private static string ExtractOutputJson(string html)
        {
            var anchor = html.IndexOf("window.$output", StringComparison.Ordinal);
            if (anchor < 0) return null;
            var start = html.IndexOf('{', anchor);
            if (start < 0) return null;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = start; i < html.Length; i++)
            {
                var ch = html[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) return html.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        private static OnlineSearchPage TryV3Songs(string identity, OnlineCollection collection, int page)
        {
            var query = "&version=9108&plat=0&page=" + page + "&pagesize=" + PageSize + "&area_code=1&" + identity;
            foreach (var endpoint in new[] { SongsV3Http, SongsV3Https })
            {
                try
                {
                    var response = OnlineHttp.Execute(endpoint + "?" + query.TrimStart('&'),
                        headers: KugouHeaders("https://www.kugou.com/"), maxBytes: 8 * 1024 * 1024);
                    if (!response.Ok) continue;
                    var data = OnlineMusicRepository.SafeParse(response.Body)?["data"] as JObject;
                    if (data == null) continue;
                    var built = BuildSongPage(data, collection, page);
                    if (built.Tracks.Count > 0 || built.Total > 0) return built;
                }
                catch
                {
                    // 换下一个协议再试。
                }
            }
            return null;
        }

        private static JObject TryV3Info(string id)
        {
            var query = "specialid=" + Uri.EscapeDataString(id) + "&plat=0&version=9108&area_code=1";
            foreach (var endpoint in new[] { InfoV3Http, InfoV3Https })
            {
                try
                {
                    var response = OnlineHttp.Execute(endpoint + "?" + query,
                        headers: KugouHeaders("https://www.kugou.com/"), maxBytes: 1024 * 1024);
                    if (!response.Ok) continue;
                    var data = OnlineMusicRepository.SafeParse(response.Body)?["data"] as JObject;
                    if (data != null && !string.IsNullOrWhiteSpace(data.Value<string>("specialname"))) return data;
                }
                catch
                {
                    // 元数据拿不到时继续走其它接口。
                }
            }
            return null;
        }

        private static OnlineSearchPage BuildSongPage(JObject data, OnlineCollection collection, int page)
        {
            var raw = data?["info"] as JArray ?? data?["list"] as JArray ?? new JArray();
            var tracks = raw.OfType<JObject>().Select(OnlineMusicRepository.ParseKugouTrack)
                .Where(t => t != null).ToList();
            var total = Math.Max(Math.Max(collection == null ? 0 : collection.TrackCount,
                PositiveInt(data, "total", "songcount", "song_count", "count")), tracks.Count);
            return new OnlineSearchPage
            {
                Tracks = tracks,
                Page = page,
                HasNext = total > 0 ? page * PageSize < total : raw.Count >= PageSize,
                Total = total
            };
        }

        private static Dictionary<string, object> InfoParameters(string id)
        {
            var parameters = BaseParameters();
            ApplyIdentity(parameters, id);
            return parameters;
        }

        private static Dictionary<string, object> SongParameters(string id, int page)
        {
            var parameters = BaseParameters();
            parameters["plat"] = 0;
            parameters["version"] = 8000;
            parameters["page"] = page;
            parameters["pagesize"] = PageSize;
            ApplyIdentity(parameters, id);
            return parameters;
        }

        private static void ApplyIdentity(Dictionary<string, object> parameters, string id)
        {
            if (IsNumeric(id))
            {
                parameters["specialid"] = id;
                return;
            }
            // 非数字标识按新版 global_collection_id 处理。
            parameters["specialid"] = 0;
            parameters["global_specialid"] = id;
        }

        private static Dictionary<string, object> BaseParameters()
        {
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new Dictionary<string, object>
            {
                ["appid"] = 1058,
                ["format"] = "jsonp",
                ["srcappid"] = 2919,
                ["clientver"] = 20000,
                ["clienttime"] = stamp,
                ["mid"] = stamp,
                ["uuid"] = stamp,
                ["dfid"] = "-"
            };
        }

        private static Dictionary<string, string> KugouHeaders(string referer)
        {
            var headers = new Dictionary<string, string> { ["User-Agent"] = MobileUserAgent };
            if (!string.IsNullOrWhiteSpace(referer)) headers["Referer"] = referer;
            return headers;
        }

        private static bool IsNumeric(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (var ch in value)
            {
                if (ch < '0' || ch > '9') return false;
            }
            return true;
        }

        /// <summary>新版酷狗分享链接给的是 collection_ 开头的 global_collection_id。</summary>
        private static bool IsCollectionId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.StartsWith("collection_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>酷狗短链分享给的是 gcid_ 开头的分享标识。</summary>
        private static bool IsGcid(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.StartsWith("gcid_", StringComparison.OrdinalIgnoreCase);
        }

        public static OnlineCollection CollectionFromInfo(string id, JObject data)
        {
            var title = Clean(data.Value<string>("specialname"));
            if (string.IsNullOrWhiteSpace(title)) title = Clean(data.Value<string>("special_name"));
            if (string.IsNullOrWhiteSpace(title)) title = "酷狗歌单 " + id;
            var owner = Clean(data.Value<string>("nickname"));
            if (string.IsNullOrWhiteSpace(owner)) owner = Clean(data.Value<string>("username"));
            var count = PositiveInt(data, "songcount", "song_count", "count", "total");
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(owner)) parts.Add(owner);
            if (count > 0) parts.Add(count + " 首");
            var image = data.Value<string>("imgurl");
            if (string.IsNullOrWhiteSpace(image)) image = data.Value<string>("img") ?? string.Empty;
            return new OnlineCollection
            {
                Platform = OnlinePlatform.Kugou,
                Id = id,
                Title = title,
                Subtitle = string.Join(" · ", parts),
                ArtworkUrl = image.Replace("{size}", "400").Replace("http://", "https://"),
                Kind = OnlineCollectionKind.Playlist,
                TrackCount = count
            };
        }

        public static string Signature(IDictionary<string, object> parameters)
        {
            var builder = new StringBuilder(SignatureSalt);
            foreach (var pair in parameters.Where(p => p.Value != null).OrderBy(p => p.Key, StringComparer.Ordinal))
                builder.Append(pair.Key).Append('=').Append(pair.Value);
            builder.Append(SignatureSalt);
            return TextUtil.Md5Hex(builder.ToString());
        }

        private static string AndroidSignature(string parameters, string body)
        {
            var parts = parameters.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            Array.Sort(parts, StringComparer.Ordinal);
            var builder = new StringBuilder(AndroidSignatureSalt);
            foreach (var part in parts) builder.Append(part);
            builder.Append(body).Append(AndroidSignatureSalt);
            return TextUtil.Md5Hex(builder.ToString());
        }

        private static JObject TryRequest(string endpoint, Dictionary<string, object> parameters)
        {
            try
            {
                return Request(endpoint, parameters);
            }
            catch
            {
                return null;
            }
        }

        private static JObject Request(string endpoint, Dictionary<string, object> parameters)
        {
            var stamp = Convert.ToString(parameters["clienttime"]);
            var query = new Dictionary<string, object>(parameters) { ["signature"] = Signature(parameters) };
            var response = OnlineHttp.Execute(OnlineHttp.Query(endpoint, query), headers: new Dictionary<string, string>
            {
                ["Referer"] = ShareReferer,
                ["User-Agent"] = MobileUserAgent,
                ["mid"] = stamp,
                ["dfid"] = "-",
                ["clienttime"] = stamp
            }, maxBytes: 8 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException("酷狗音乐返回 HTTP " + response.StatusCode);
            var root = OnlineMusicRepository.SafeParse(response.Body);
            var errorCode = root.Value<int?>("errcode") ?? root.Value<int?>("err_code") ?? 0;
            if (errorCode != 0)
            {
                var message = root.Value<string>("errmsg");
                if (string.IsNullOrWhiteSpace(message)) message = root.Value<string>("error");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "酷狗音乐歌单读取失败" : message);
            }
            var data = root["data"] as JObject;
            if (data == null) throw new InvalidOperationException("酷狗音乐没有返回歌单数据");
            return data;
        }

        private static int PositiveInt(JObject value, params string[] names)
        {
            if (value == null) return 0;
            foreach (var name in names)
            {
                var token = value[name];
                if (token == null) continue;
                var number = token.Value<int?>();
                if (number.HasValue && number.Value > 0) return number.Value;
            }
            return 0;
        }

        private static string Clean(string value) => TextUtil.HtmlDecode(value ?? string.Empty);
    }
}

namespace TzPlayer.Win.Online
{
    /// <summary>网易云音乐公开 / 授权歌单读取。</summary>
    internal static class NeteasePlaylistLoader
    {
        private const int PageSize = 100;
        private const string PlaylistDetail = "https://music.163.com/api/v3/playlist/detail";
        private const string V6PlaylistDetail = "https://music.163.com/api/v6/playlist/detail";
        private const string SongDetail = "https://music.163.com/api/v3/song/detail";

        public static OnlineCollection Metadata(string id, string cookie = "")
        {
            var playlist = PlaylistRoot(id, cookie)["playlist"] as JObject;
            if (playlist == null) throw new InvalidOperationException("网易云音乐没有返回歌单信息");
            return CollectionFromPlaylist(id, playlist);
        }

        public static OnlineSearchPage Tracks(string id, int page, string cookie = "")
        {
            var playlist = PlaylistRoot(id, cookie)["playlist"] as JObject;
            if (playlist == null) throw new InvalidOperationException("网易云音乐没有返回歌单信息");
            var safePage = Math.Max(1, page);
            var trackIds = TrackIds(playlist);
            var advertisedTotal = Math.Max(0, playlist.Value<int?>("trackCount") ?? 0);
            var embedded = playlist["tracks"] as JArray ?? new JArray();
            var total = Math.Max(Math.Max(trackIds.Count, advertisedTotal), embedded.Count);
            if (trackIds.Count == 0)
            {
                trackIds.AddRange(embedded.OfType<JObject>()
                    .Select(item => item.Value<string>("id"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            var pageIds = PageTrackIds(trackIds, safePage);
            var records = new List<JObject>();
            if (pageIds.Count > 0)
            {
                var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
                foreach (var item in embedded.OfType<JObject>())
                {
                    var recordId = item.Value<string>("id");
                    if (!string.IsNullOrWhiteSpace(recordId) && !byId.ContainsKey(recordId)) byId[recordId] = item;
                }
                var missing = new List<string>();
                foreach (var want in pageIds)
                {
                    JObject record;
                    if (byId.TryGetValue(want, out record)) records.Add(record);
                    else missing.Add(want);
                }
                if (missing.Count > 0) records.AddRange(OrderSongRecords(missing, SongRecords(missing, cookie)));
            }
            else
            {
                records.AddRange(PageJson(embedded, safePage));
            }

            var tracks = records.Select(OnlineMusicRepository.ParseNeteaseTrack).Where(t => t != null).ToList();
            var hasNext = trackIds.Count > 0
                ? safePage * PageSize < trackIds.Count
                : safePage * PageSize < embedded.Count;
            return new OnlineSearchPage { Tracks = tracks, Page = safePage, HasNext = hasNext, Total = total };
        }

        public static List<OnlineCollection> UserPlaylists(string userId, string cookie)
        {
            var result = new List<OnlineCollection>();
            var offset = 0;
            for (var page = 0; page < 20; page++)
            {
                var url = OnlineHttp.Query("https://music.163.com/api/user/playlist",
                    new Dictionary<string, object> { ["uid"] = userId, ["limit"] = 100, ["offset"] = offset });
                var response = OnlineHttp.Execute(url, headers: Headers(cookie), maxBytes: 8 * 1024 * 1024);
                if (!response.Ok) throw new InvalidOperationException("网易云音乐账号歌单读取失败");
                var root = OnlineMusicRepository.SafeParse(response.Body);
                if (root.Value<int?>("code") != 200) throw new InvalidOperationException("网易云音乐账号歌单读取失败，请重新授权");
                var list = root["playlist"] as JArray ?? new JArray();
                var before = result.Count;
                foreach (var item in list.OfType<JObject>())
                {
                    var id = item.Value<string>("id");
                    var title = (item.Value<string>("name") ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                    if (result.Any(c => c.Id == id)) continue;
                    result.Add(new OnlineCollection
                    {
                        Platform = OnlinePlatform.Netease,
                        Id = id,
                        Title = title,
                        Subtitle = "授权账号歌单",
                        ArtworkUrl = (item.Value<string>("coverImgUrl") ?? string.Empty).Replace("http://", "https://"),
                        Kind = OnlineCollectionKind.Playlist,
                        TrackCount = Math.Max(0, item.Value<int?>("trackCount") ?? 0),
                        AccountScoped = true
                    });
                }
                if (!(root.Value<bool?>("more") ?? false) || list.Count == 0 || result.Count == before) break;
                offset += 100;
            }
            return result;
        }

        public static List<string> PageTrackIds(List<string> trackIds, int page)
        {
            var start = (Math.Max(1, page) - 1) * PageSize;
            if (start < 0 || start >= trackIds.Count) return new List<string>();
            return trackIds.Skip(start).Take(PageSize).ToList();
        }

        public static List<JObject> OrderSongRecords(List<string> trackIds, JArray records)
        {
            var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var record in records.OfType<JObject>())
            {
                var id = record.Value<string>("id");
                if (!string.IsNullOrWhiteSpace(id) && !byId.ContainsKey(id)) byId[id] = record;
            }
            var result = new List<JObject>();
            foreach (var id in trackIds)
            {
                if (byId.TryGetValue(id, out var record)) result.Add(record);
            }
            return result;
        }

        private static List<string> TrackIds(JObject playlist)
        {
            var raw = playlist["trackIds"] as JArray;
            var result = new List<string>();
            if (raw == null) return result;
            foreach (var item in raw.OfType<JObject>())
            {
                var id = item.Value<string>("id");
                if (!string.IsNullOrWhiteSpace(id) && !result.Contains(id)) result.Add(id);
            }
            return result;
        }

        private static List<JObject> PageJson(JArray records, int page)
        {
            var start = (Math.Max(1, page) - 1) * PageSize;
            var end = Math.Min(start + PageSize, records.Count);
            var result = new List<JObject>();
            for (var i = start; i < end; i++)
            {
                if (records[i] is JObject item) result.Add(item);
            }
            return result;
        }

        private static OnlineCollection CollectionFromPlaylist(string id, JObject playlist)
        {
            var title = (playlist.Value<string>("name") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("网易云音乐歌单缺少名称");
            var owner = ((playlist["creator"] as JObject)?.Value<string>("nickname") ?? string.Empty).Trim();
            var count = Math.Max(TrackIds(playlist).Count, Math.Max(0, playlist.Value<int?>("trackCount") ?? 0));
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(owner)) parts.Add(owner);
            if (count > 0) parts.Add(count + " 首");
            return new OnlineCollection
            {
                Platform = OnlinePlatform.Netease,
                Id = id,
                Title = title,
                Subtitle = string.Join(" · ", parts),
                ArtworkUrl = (playlist.Value<string>("coverImgUrl") ?? string.Empty).Replace("http://", "https://"),
                Kind = OnlineCollectionKind.Playlist,
                TrackCount = count
            };
        }

        private static JObject PlaylistRoot(string id, string cookie)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("网易云音乐歌单标识无效");
            // v6 会一次性返回全部曲目详情（v3 只返回前 10 首，需要再补 song/detail），优先 v6。
            var attempts = new[]
            {
                OnlineHttp.Query(V6PlaylistDetail, new Dictionary<string, object> { ["id"] = id, ["n"] = 1000, ["s"] = 0 }),
                OnlineHttp.Query(PlaylistDetail, new Dictionary<string, object> { ["id"] = id, ["n"] = 1000, ["s"] = 8 })
            };
            Exception last = null;
            foreach (var url in attempts)
            {
                try
                {
                    var response = OnlineHttp.Execute(url, headers: Headers(cookie), maxBytes: 16 * 1024 * 1024);
                    if (!response.Ok)
                    {
                        last = new InvalidOperationException("网易云音乐返回 HTTP " + response.StatusCode);
                        continue;
                    }
                    var root = OnlineMusicRepository.SafeParse(response.Body);
                    var code = root.Value<int?>("code") ?? 200;
                    if (code != 200)
                    {
                        last = new InvalidOperationException("网易云音乐歌单读取失败");
                        continue;
                    }
                    if (root["playlist"] is JObject) return root;
                    last = new InvalidOperationException("网易云音乐没有返回歌单信息");
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw last ?? new InvalidOperationException("网易云音乐歌单读取失败");
        }

        private static JArray SongRecords(List<string> ids, string cookie)
        {
            if (ids.Count == 0) return new JArray();
            var payload = new JArray();
            foreach (var id in ids) payload.Add(new JObject { ["id"] = id });
            var body = "c=" + Uri.EscapeDataString(payload.ToString(Formatting.None));
            var headers = Headers(cookie);
            headers["Content-Type"] = "application/x-www-form-urlencoded; charset=UTF-8";
            var response = OnlineHttp.Execute(SongDetail, "POST", headers,
                Encoding.UTF8.GetBytes(body), maxBytes: 8 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException("网易云音乐歌曲读取失败");
            var root = OnlineMusicRepository.SafeParse(response.Body);
            if ((root.Value<int?>("code") ?? 200) != 200) throw new InvalidOperationException("网易云音乐歌曲读取失败");
            return (root["songs"] as JArray) ?? (root["data"] as JObject)?["songs"] as JArray ?? new JArray();
        }

        private static Dictionary<string, string> Headers(string cookie)
        {
            var headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://music.163.com/",
                ["User-Agent"] = OnlineHttp.BrowserUserAgent
            };
            if (!string.IsNullOrWhiteSpace(cookie)) headers["Cookie"] = cookie;
            return headers;
        }
    }
}
