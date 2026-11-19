using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TzPlayer.Win.Online
{
    // 对照安卓 OnlineLyricsRepository：按标题、歌手、时长匹配，失败来源互不影响。
    internal sealed class LyricsProviderClient
    {
        internal sealed class Candidate
        {
            internal string Source, Id, Title, Artist, Text, AccessKey;
            internal long DurationMs;
        }

        private readonly CancellationToken _cancellation;
        private readonly Func<string, string, JObject, string, string> _request;
        internal LyricsProviderClient(CancellationToken cancellation,
            Func<string, string, JObject, string, string> request = null)
        {
            _cancellation = cancellation;
            _request = request ?? Request;
        }

        internal string Fetch(OnlineTrack wanted)
        {
            var platform = wanted.Platform?.Id;
            var direct = Try(() => platform == "netease" ? NeteaseLyric(wanted.Id) : platform == "qq" ? QqLyric(wanted.Id) : null);
            if (IsTimed(direct)) return direct;
            var query = string.Join(" ", new[] { wanted.Title, wanted.Artist }.Where(value => !string.IsNullOrWhiteSpace(value)));

            // Fast path: the common case should finish after one catalog lookup plus one lyric request.
            var quick = Resolve(wanted, () => NeteaseSearch(query));
            var best = Best(wanted, quick, 0.65);
            if (best != null) return best.Text;

            quick = Resolve(wanted, () => LrcLibSearch(wanted));
            best = Best(wanted, quick, 0.65);
            if (best != null) return best.Text;

            if (platform == "kugou")
            {
                var hash = Try(() => JObject.Parse(wanted.MusicInfoJson ?? "{}").Value<string>("hash")) ?? wanted.Id;
                var exactList = Resolve(wanted, () => KugouSearch(query, wanted.DurationMs, hash));
                var exact = exactList.OrderByDescending(item => Score(wanted, item)).FirstOrDefault();
                if (exact != null && Score(wanted, exact) >= 0.88) return exact.Text;
            }

            var usable = new List<Candidate>();
            usable.AddRange(quick);
            foreach (var source in new[] { "qqdirect", "kugou" })
            {
                _cancellation.ThrowIfCancellationRequested();
                usable.AddRange(Resolve(wanted, () =>
                {
                    if (source == "qqdirect") return QqSearch(query);
                    if (source == "kugou")
                    {
                        var native = TryList(() => KugouSearch(query, wanted.DurationMs, null));
                        if (native.Count > 0) return native;
                    }
                    return new List<Candidate>();
                }));
                best = Best(wanted, usable, 0.65);
                if (best != null) return best.Text;
            }
            return null;
        }

        private static Candidate Best(OnlineTrack wanted, IEnumerable<Candidate> candidates, double threshold)
        {
            var best = candidates.Where(candidate => TitleSimilarity(wanted.Title, candidate.Title) >= 0.55)
                .OrderByDescending(candidate => Score(wanted, candidate)).FirstOrDefault();
            return best != null && Score(wanted, best) >= threshold ? best : null;
        }

        private List<Candidate> Resolve(OnlineTrack wanted, Func<List<Candidate>> search)
        {
            var result = new List<Candidate>();
            foreach (var item in TryList(search).OrderByDescending(candidate => Score(wanted, candidate)).Take(4))
            {
                _cancellation.ThrowIfCancellationRequested();
                if (TitleSimilarity(wanted.Title, item.Title) < 0.55) continue;
                var text = item.Text;
                if (!IsTimed(text)) text = Try(() => Content(item));
                if (!IsTimed(text)) continue;
                item.Text = text;
                result.Add(item);
                if (Score(wanted, item) >= 0.90) break;
            }
            return result;
        }

        internal static bool IsTimed(string text) => !string.IsNullOrWhiteSpace(text) &&
            Regex.IsMatch(text, @"(?m)^\[(?:\d{1,3}:)?\d{1,2}:\d{2}(?:[\.:]\d{1,3})?\]");
        internal static double Score(OnlineTrack wanted, Candidate item)
        {
            var title = TitleSimilarity(wanted.Title, item.Title);
            var hasArtist = !string.IsNullOrWhiteSpace(wanted.Artist) && !string.IsNullOrWhiteSpace(item.Artist);
            var artist = hasArtist ? Similarity(Normalize(wanted.Artist), Normalize(item.Artist)) : 0;
            var duration = wanted.DurationMs <= 0 || item.DurationMs <= 0 ? 0.5
                : Math.Max(0, 1 - Math.Abs(wanted.DurationMs - item.DurationMs) / 20000.0);
            var timed = IsTimed(item.Text) ? 1 : 0.35;
            return hasArtist ? title * .60 + artist * .24 + duration * .10 + timed * .06
                : title * .80 + duration * .13 + timed * .07;
        }
        private static string Normalize(string value) => Regex.Replace((value ?? "").ToLowerInvariant(), @"[\p{P}\p{S}\s]+", "");
        private static double TitleSimilarity(string a, string b) => Similarity(
            Normalize(Regex.Split(a ?? "", @"[（(]")[0]), Normalize(Regex.Split(b ?? "", @"[（(]")[0]));
        private static double Similarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            if (a.Contains(b) || b.Contains(a)) return .82 + .18 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            var current = new int[b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var swap = previous; previous = current; current = swap;
            }
            return 1 - previous[b.Length] / (double)Math.Max(a.Length, b.Length);
        }

        private string Content(Candidate item)
        {
            if (item.Source == "netease") return NeteaseLyric(item.Id);
            if (item.Source == "qqdirect") return QqLyric(item.Id);
            var direct = Try(() =>
            {
                if (!string.IsNullOrEmpty(item.AccessKey)) return KugouLyric(item.Id, item.AccessKey);
                var token = DecodeToken(item.Id);
                if (item.Source == "kugou") return KugouLyric((string)token["id"], (string)token["accesskey"]);
                if (item.Source == "qqmusic") return QqLyric((string)token["songmid"]);
                return null;
            });
            if (IsTimed(direct)) return direct;
            return (string)Json("https://voxly.cn/v1/lyrics/content", "https://voxly.cn/",
                new JObject { ["source"] = item.Source, ["song_id"] = item.Id })?["text"];
        }
        private List<Candidate> NeteaseSearch(string query)
        {
            var root = Json("https://music.163.com/api/cloudsearch/pc?s=" + Escape(query) + "&type=1&limit=12&offset=0&total=true");
            return (root?.SelectToken("result.songs") as JArray ?? new JArray()).Select(song => new Candidate
            {
                Source = "netease", Id = (string)song["id"], Title = (string)song["name"],
                Artist = string.Join("/", (song["ar"] as JArray ?? song["artists"] as JArray ?? new JArray()).Select(artist => (string)artist["name"])),
                DurationMs = (long?)(song["dt"] ?? song["duration"]) ?? 0
            }).ToList();
        }
        private string NeteaseLyric(string id) => (string)Json(
            "https://music.163.com/api/song/lyric?id=" + Escape(id) + "&lv=-1&kv=-1&tv=-1")?.SelectToken("lrc.lyric");
        private string QqLyric(string mid)
        {
            if (string.IsNullOrWhiteSpace(mid)) return null;
            var lyric = WebUtility.HtmlDecode((string)Json("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid=" + Escape(mid)
                + "&format=json&nobase64=1&g_tk=5381", "https://y.qq.com/")?["lyric"]);
            return IsTimed(lyric) ? lyric : Try(() => Encoding.UTF8.GetString(Convert.FromBase64String(lyric ?? "")));
        }
        private List<Candidate> QqSearch(string query)
        {
            var root = Json("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=8&w=" + Escape(query), "https://y.qq.com/");
            return (root?.SelectToken("data.song.list") as JArray ?? new JArray()).Select(song => new Candidate
            {
                Source = "qqdirect",
                Id = (string)song["songmid"] ?? (string)song["mid"],
                Title = (string)song["songname"] ?? (string)song["name"],
                Artist = string.Join("/", (song["singer"] as JArray ?? new JArray()).Select(artist => (string)artist["name"])),
                DurationMs = ((long?)song["interval"] ?? 0) * 1000
            }).Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToList();
        }
        private List<Candidate> KugouSearch(string query, long duration, string hash)
        {
            var url = "https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=" + Escape(query);
            if (duration > 0) url += "&duration=" + duration;
            if (!string.IsNullOrWhiteSpace(hash)) url += "&hash=" + Escape(hash);
            return (Json(url, "https://www.kugou.com/")?["candidates"] as JArray ?? new JArray()).Select(item => new Candidate
            {
                Source = "kugou", Id = (string)item["id"], AccessKey = (string)item["accesskey"],
                Title = (string)item["song"], Artist = (string)item["singer"], DurationMs = (long?)item["duration"] ?? 0
            }).ToList();
        }
        private string KugouLyric(string id, string accessKey)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(accessKey)) return null;
            var content = (string)Json("https://lyrics.kugou.com/download?ver=1&client=pc&id=" + Escape(id)
                + "&accesskey=" + Escape(accessKey) + "&fmt=lrc&charset=utf8", "https://www.kugou.com/")?["content"];
            return string.IsNullOrWhiteSpace(content) ? null : Encoding.UTF8.GetString(Convert.FromBase64String(content));
        }
        private List<Candidate> VoxlySearch(string query, string source)
        {
            return (Json("https://voxly.cn/v1/lyrics/search", "https://voxly.cn/", new JObject { ["query"] = query, ["source"] = source })?["results"] as JArray ?? new JArray())
                .Select(item => new Candidate
                {
                    Source = (string)item["source"] ?? source, Id = (string)item["song_id"], Title = (string)item["title"],
                    Artist = (string)item["artist"], DurationMs = (long?)item["duration_ms"] ?? 0
                }).ToList();
        }
        private List<Candidate> LrcLibSearch(OnlineTrack wanted)
        {
            var query = string.IsNullOrWhiteSpace(wanted.Artist) ? "q=" + Escape(wanted.Title)
                : "track_name=" + Escape(wanted.Title) + "&artist_name=" + Escape(wanted.Artist);
            return JArray.Parse(_request("https://lrclib.net/api/search?" + query, "GET", null, "https://lrclib.net/"))
                .Select(item => new Candidate
                {
                    Source = "lrclib", Id = (string)item["id"], Title = (string)item["trackName"], Artist = (string)item["artistName"],
                    DurationMs = (long)(((double?)item["duration"] ?? 0) * 1000), Text = (string)item["syncedLyrics"]
                }).Where(item => IsTimed(item.Text)).ToList();
        }
        private static JObject DecodeToken(string value)
        {
            var base64 = (value ?? "").Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }
        private JObject Json(string url, string referer = "https://music.163.com/", JObject body = null)
            => JObject.Parse(_request(url, body == null ? "GET" : "POST", body, referer));
        private static string Escape(string value) => Uri.EscapeDataString(value ?? "");
        private string Try(Func<string> action)
        {
            _cancellation.ThrowIfCancellationRequested();
            try { return action(); }
            catch (OperationCanceledException) { throw; }
            catch { _cancellation.ThrowIfCancellationRequested(); return null; }
        }
        private List<Candidate> TryList(Func<List<Candidate>> action)
        {
            _cancellation.ThrowIfCancellationRequested();
            try { return action(); }
            catch (OperationCanceledException) { throw; }
            catch { _cancellation.ThrowIfCancellationRequested(); return new List<Candidate>(); }
        }
        private string Request(string url, string method, JObject body, string referer)
        {
            _cancellation.ThrowIfCancellationRequested();
            var handle = new OnlineHttpRequestHandle();
            using (_cancellation.Register(handle.Cancel))
            {
                var headers = new Dictionary<string, string> { ["Referer"] = referer, ["Content-Type"] = "application/json; charset=utf-8" };
                var response = OnlineHttp.Execute(url, method, headers,
                    body == null ? null : Encoding.UTF8.GetBytes(body.ToString(Formatting.None)), timeoutMs: 4500, handle: handle);
                _cancellation.ThrowIfCancellationRequested();
                if (!response.Ok) throw new InvalidOperationException("歌词源返回 HTTP " + response.StatusCode);
                return response.Body;
            }
        }
    }
}
