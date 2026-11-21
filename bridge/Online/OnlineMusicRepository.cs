using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    /// <summary>在线音乐主仓库：搜索 / 解析播放地址 / 歌单 / 音源导入。</summary>
    public sealed class OnlineMusicRepository
    {
        public const string DefaultSourceCatalogUrl = "https://dl.voxly.cn/live/online-sources.json";

        private readonly object _runtimeSync = new object();
        private LxSourceRuntime _runtime;
        private string _runtimeScript;

        public OnlineMusicRepository(OnlineSourceStore sourceStore, OnlineAccountStore accountStore)
        {
            Sources = sourceStore ?? throw new ArgumentNullException(nameof(sourceStore));
            Accounts = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        }

        public OnlineSourceStore Sources { get; }
        public OnlineAccountStore Accounts { get; }

        // ----------------------------------------------------------------- 音源管理

        public LxSourceDescriptor ImportSource(string subscriptionUrl, string displayName)
        {
            var cleanUrl = (subscriptionUrl ?? string.Empty).Trim();
            if (cleanUrl.Length < 8 || cleanUrl.Length > 4096) throw new InvalidOperationException("订阅地址无效");
            var response = OnlineHttp.Execute(cleanUrl, maxBytes: 2 * 1024 * 1024, useSystemProxy: true);
            if (!response.Ok) throw new InvalidOperationException(HttpError(response.StatusCode));
            var script = response.Body.TrimStart('\uFEFF');
            if (script.Length < 32) throw new InvalidOperationException("订阅返回的音源脚本为空");
            return ImportScript(script, cleanUrl, displayName);
        }

        /// <summary>从本地 LX 音源脚本文件（.js）导入。</summary>
        public LxSourceDescriptor ImportLocalFile(string filePath, string displayName)
        {
            var path = (filePath ?? string.Empty).Trim().Trim('"');
            if (!System.IO.File.Exists(path)) throw new InvalidOperationException("找不到音源脚本文件：" + path);
            var info = new System.IO.FileInfo(path);
            if (info.Length > 2L * 1024 * 1024) throw new InvalidOperationException("音源脚本文件过大");
            var script = System.IO.File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
            if (script.Length < 32) throw new InvalidOperationException("音源脚本内容为空");
            if (string.IsNullOrWhiteSpace(displayName)) displayName = System.IO.Path.GetFileNameWithoutExtension(path);
            return ImportScript(script, string.Empty, displayName, "file:" + System.IO.Path.GetFullPath(path));
        }

        private LxSourceDescriptor ImportScript(string script, string subscriptionUrl, string displayName, string origin = null)
        {
            var runtime = LxSourceRuntime.Create(script, BuildRuntimeKey(script), TimeSpan.FromSeconds(25));
            try
            {
                var descriptor = runtime.Descriptor;
                lock (_runtimeSync)
                {
                    Sources.Save(subscriptionUrl, script, descriptor, displayName, origin);
                    _runtime?.Dispose();
                    _runtime = runtime;
                    _runtimeScript = script;
                }
                return descriptor;
            }
            catch
            {
                runtime.Dispose();
                throw;
            }
        }

        public void ResetSource(string id = null)
        {
            lock (_runtimeSync)
            {
                _runtime?.Dispose();
                _runtime = null;
                _runtimeScript = null;
                Sources.Remove(id);
            }
        }

        public void EditSource(string id, string revision, string script)
        {
            if (string.IsNullOrWhiteSpace(script) || script.IndexOf('\0') >= 0)
                throw new InvalidOperationException("音源脚本为空或含有无效字符");
            if (Encoding.UTF8.GetByteCount(script) > 2 * 1024 * 1024)
                throw new InvalidOperationException("音源脚本不能超过 2 MB");
            if (OnlineSourceStore.Revision(Sources.Find(id)) != revision)
                throw new InvalidOperationException("音源已被其他操作修改，请重新打开后编辑；本次内容已保留");
            var descriptor = ValidateScript(script);
            Sources.ReplaceScript(id, revision, script, descriptor);
            ResetRuntimeOnly();
        }

        public SourceUpdateResult UpdateSource(string id, string revision, Action<string> progress = null)
        {
            var source = Sources.Find(id);
            if (OnlineSourceStore.Revision(source) != revision)
                throw new InvalidOperationException("音源已被其他操作修改，请重新打开后再更新");
            SourceUpdateNotice notice = null;
            progress?.Invoke("正在执行音源内置更新检查…");
            try
            {
                using (var runtime = LxSourceRuntime.Create(source.Script, BuildRuntimeKey(source.Script), TimeSpan.FromSeconds(25)))
                {
                    if (runtime.UpdateChecked.Wait(TimeSpan.FromSeconds(12))) notice = runtime.UpdateChecked.Result;
                }
            }
            catch
            {
                throw new InvalidOperationException("音源自检失败，请先编辑或重新导入；原音源未修改");
            }
            progress?.Invoke(notice == null ? "未收到更新提示，正在读取音源地址…" : "正在读取音源更新地址…");

            var candidates = new List<string>();
            if (notice != null && !string.IsNullOrWhiteSpace(notice.Url)) candidates.Add(notice.Url);
            else if (notice == null && !string.IsNullOrWhiteSpace(source.Url)) candidates.Add(source.Url);
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var response = OnlineHttp.Execute(candidate, maxBytes: 2 * 1024 * 1024, useSystemProxy: true);
                    if (!response.Ok) continue;
                    var script = response.Body.TrimStart('\uFEFF');
                    if (script.Length < 32) continue;
                    progress?.Invoke("正在验证新版音源脚本…");
                    var descriptor = ValidateScript(script);
                    Sources.ReplaceScript(id, revision, script, descriptor);
                    ResetRuntimeOnly();
                    progress?.Invoke("音源更新完成");
                    return new SourceUpdateResult { Descriptor = descriptor };
                }
                catch
                {
                    // A source may return a human-facing update page. Keep the
                    // alert and let the UI offer to open it.
                }
            }
            if (notice != null) {
                progress?.Invoke("音源提供更新提示");
                return new SourceUpdateResult { Notice = notice };
            }
            throw new InvalidOperationException("没有发现可下载的新版音源");
        }

        private static LxSourceDescriptor ValidateScript(string script)
        {
            try
            {
                using (var runtime = LxSourceRuntime.Create(script, BuildRuntimeKey(script), TimeSpan.FromSeconds(25)))
                    return runtime.Descriptor;
            }
            catch
            {
                // Runtime errors can echo script text such as cookies. Do not
                // expose them through the bridge when validating private code.
                throw new InvalidOperationException("脚本校验或初始化失败，请检查语法和网络；原音源未修改");
            }
        }

        private static string BuildRuntimeKey(string script)
        {
            var hash = 17;
            unchecked { foreach (var ch in script) hash = hash * 31 + ch; }
            return "lx_" + (uint)hash;
        }

        private LxSourceRuntime EnsureRuntime()
        {
            var stored = Sources.Current;
            if (stored == null || string.IsNullOrWhiteSpace(stored.Script)) throw new InvalidOperationException("尚未导入在线音源");
            lock (_runtimeSync)
            {
                if (_runtime != null && string.Equals(_runtimeScript, stored.Script, StringComparison.Ordinal)) return _runtime;
                _runtime?.Dispose();
                _runtime = LxSourceRuntime.Create(stored.Script, BuildRuntimeKey(stored.Script), TimeSpan.FromSeconds(25));
                _runtimeScript = stored.Script;
                return _runtime;
            }
        }

        /// <summary>供短生命周期桥接进程在退出前释放脚本线程，不删除已导入音源。</summary>
        public void ResetRuntimeOnly()
        {
            lock (_runtimeSync)
            {
                _runtime?.Dispose();
                _runtime = null;
                _runtimeScript = null;
            }
        }

        // ----------------------------------------------------------------- 搜索

        public OnlineSearchPage Search(OnlinePlatform platform, string query, int page = 1, int limit = 30)
        {
            var clean = (query ?? string.Empty).Trim();
            if (clean.Length == 0) throw new InvalidOperationException("请输入歌曲名或歌手");

            var state = Sources.State;
            if (state.Imported)
            {
                var actions = state.Descriptor.ActionsFor(platform);
                var searchAction = actions.FirstOrDefault(IsSearchAction);
                if (searchAction != null)
                {
                    var runtime = EnsureRuntime();
                    var result = SearchWithRuntime(runtime, searchAction, state.Descriptor.MatchSourceId(platform),
                        platform, clean, page, limit, state.Descriptor.QualitiesFor(platform));
                    return SortTracks(result);
                }
            }

            if (ReferenceEquals(platform, OnlinePlatform.Qq)) return SortTracks(SearchQq(clean, page, limit));
            if (ReferenceEquals(platform, OnlinePlatform.Kugou)) return SortTracks(SearchKugou(clean, page, limit));
            if (ReferenceEquals(platform, OnlinePlatform.Netease)) return SortTracks(SearchNetease(clean, page, limit));
            var extended = ExtendedCatalog.Search(platform, clean, page, limit);
            // The public Qishui catalog ranks candidates locally by query
            // relevance. Preserve that order instead of applying title sort.
            return ReferenceEquals(platform, OnlinePlatform.Qishui) ? extended : SortTracks(extended);
        }

        private static bool IsSearchAction(string action) =>
            string.Equals(action, "musicSearch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "search", StringComparison.OrdinalIgnoreCase);

        private static OnlineSearchPage SortTracks(OnlineSearchPage page)
        {
            page.Tracks = page.Tracks.OrderBy(EpisodeNumber).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            return page;
        }

        private static int EpisodeNumber(OnlineTrack track)
        {
            var match = System.Text.RegularExpressions.Regex.Match(track.Title ?? string.Empty, "(?i)第\\s*(\\d+)\\s*[集话期]");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number)) return number;
            return 0;
        }

        private OnlineSearchPage SearchWithRuntime(
            LxSourceRuntime runtime, string action, string source, OnlinePlatform platform,
            string query, int page, int limit, List<string> fallbackQualities)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var info = new JObject
            {
                ["keyword"] = query,
                ["name"] = query,
                ["page"] = safePage,
                ["pagesize"] = safeLimit,
                ["limit"] = safeLimit,
                ["offset"] = (long)(safePage - 1) * safeLimit,
                ["type"] = safeLimit
            };
            var raw = runtime.InvokeAsync(action, source, info.ToString(Formatting.None), TimeSpan.FromSeconds(60))
                .GetAwaiter().GetResult();
            return ParseNativeSearchResult(raw, platform, source, safePage, safeLimit, fallbackQualities);
        }

        private static OnlineSearchPage SearchQq(string query, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var url = OnlineHttp.Query("https://c.y.qq.com/soso/fcgi-bin/client_search_cp",
                new Dictionary<string, object> { ["format"] = "json", ["p"] = safePage, ["n"] = safeLimit, ["w"] = query });
            var response = OnlineHttp.Execute(url, headers: new Dictionary<string, string>
            {
                ["Referer"] = "https://y.qq.com/",
                ["User-Agent"] = OnlineHttp.BrowserUserAgent
            });
            if (!response.Ok) throw new InvalidOperationException(HttpError(response.StatusCode));
            var root = SafeParse(response.Body);
            if (root.Value<int?>("code") != 0) throw new InvalidOperationException("QQ音乐搜索失败");
            var list = root.SelectToken("data.song.list") as JArray ?? new JArray();
            var tracks = list.OfType<JObject>().Select(ParseQqTrack).Where(t => t != null).ToList();
            var total = root.SelectToken("data.song.totalnum")?.Value<int>();
            return new OnlineSearchPage { Tracks = tracks }.WithPagination(safePage, safeLimit, total, list.Count);
        }

        private static OnlineSearchPage SearchKugou(string query, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(30, limit));
            var url = OnlineHttp.Query("https://songsearch.kugou.com/song_search_v2", new Dictionary<string, object>
            {
                ["keyword"] = query, ["page"] = safePage, ["pagesize"] = safeLimit, ["userid"] = 0,
                ["clientver"] = string.Empty, ["platform"] = "WebFilter", ["filter"] = 2,
                ["iscorrection"] = 1, ["privilege_filter"] = 0, ["area_code"] = 1
            });
            var response = OnlineHttp.Execute(url, headers: new Dictionary<string, string>
            {
                ["Referer"] = "https://www.kugou.com/",
                ["User-Agent"] = OnlineHttp.BrowserUserAgent
            }, maxBytes: 4 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException(HttpError(response.StatusCode));
            var root = SafeParse(response.Body);
            if (root.Value<int?>("error_code") != 0) throw new InvalidOperationException("酷狗音乐搜索失败");
            var list = root.SelectToken("data.lists") as JArray ?? new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tracks = new List<OnlineTrack>();
            foreach (var item in list.OfType<JObject>())
            {
                var track = ParseKugouTrack(item);
                if (track != null && seen.Add(track.Id)) tracks.Add(track);
            }
            var total = root.SelectToken("data.total")?.Value<int>();
            return new OnlineSearchPage { Tracks = tracks }.WithPagination(safePage, safeLimit, total, list.Count);
        }

        private static OnlineSearchPage SearchNetease(string query, int page, int limit)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(30, limit));
            var url = OnlineHttp.Query("https://music.163.com/api/cloudsearch/pc", new Dictionary<string, object>
            {
                ["s"] = query, ["type"] = 1, ["offset"] = (safePage - 1) * safeLimit, ["limit"] = safeLimit
            });
            var response = OnlineHttp.Execute(url, headers: new Dictionary<string, string>
            {
                ["Referer"] = "https://music.163.com/",
                ["User-Agent"] = OnlineHttp.BrowserUserAgent
            }, maxBytes: 4 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException(HttpError(response.StatusCode));
            var root = SafeParse(response.Body);
            if (root.Value<int?>("code") != 200) throw new InvalidOperationException("网易云音乐搜索失败");
            var list = root.SelectToken("result.songs") as JArray ?? new JArray();
            var tracks = list.OfType<JObject>().Select(ParseNeteaseTrack).Where(t => t != null).ToList();
            var total = root.SelectToken("result.songCount")?.Value<int>();
            return new OnlineSearchPage { Tracks = tracks }.WithPagination(safePage, safeLimit, total, list.Count);
        }
        // ----------------------------------------------------------------- 音质与解析

        public List<OnlineQuality> QualitiesFor(OnlineTrack track)
        {
            var state = Sources.State;
            if (state.Imported)
            {
                var tokens = state.Descriptor.QualitiesFor(track.Platform);
                var supported = tokens.Select(OnlineQuality.FromId).Where(q => q != null).Distinct().ToList();
                if (supported.Count > 0) return OnlineQuality.All.Where(supported.Contains).ToList();
            }
            return track.AvailableQualities.Distinct().ToList();
        }

        public OnlineQuality InitialQuality(OnlineTrack track)
        {
            var choices = QualitiesFor(track);
            var preferred = Sources.State.PreferredQuality;
            if (choices.Contains(preferred)) return preferred;
            return choices.OrderByDescending(OnlineQuality.RankOf).FirstOrDefault() ?? OnlineQuality.Standard;
        }

        public List<string> RemainingQualityTokens(OnlineTrack track, OnlineQuality requested,
            IEnumerable<string> excluded = null)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            requested = requested ?? OnlineQuality.Standard;
            var state = Sources.State;
            if (!state.Imported) throw new InvalidOperationException("请先在菜单的“导入音源”中导入在线音源");
            var sourceId = state.Descriptor.MatchSourceId(track.Platform);
            if (sourceId == null) throw new InvalidOperationException("已导入音源不支持" + track.Platform.Label);

            var sourceQualities = state.Descriptor.QualitiesFor(track.Platform);
            var trackQualities = track.AvailableQualities ?? new List<OnlineQuality>();
            if ((sourceQualities == null || sourceQualities.Count == 0) && trackQualities.Count > 0)
                sourceQualities = trackQualities.Select(item => item.Id).ToList();
            var tokens = QualityTokensFor(requested, sourceQualities, trackQualities);
            if (tokens.Count == 0) tokens.Add(requested.Id);
            var skipped = new HashSet<OnlineQuality>((excluded ?? Enumerable.Empty<string>())
                .Select(OnlineQuality.FromId).Where(item => item != null));
            return tokens.Where(token => !skipped.Contains(OnlineQuality.FromId(token))).ToList();
        }

        public ResolvedOnlineMedia Resolve(OnlineTrack track, OnlineQuality requested,
            IEnumerable<string> excluded = null, Action<string> onAttempt = null)
        {
            var tokens = RemainingQualityTokens(track, requested, excluded);
            if (tokens.Count == 0) throw new InvalidOperationException("当前歌曲的可用音质均已尝试");
            var sourceId = Sources.State.Descriptor.MatchSourceId(track.Platform);
            var runtime = EnsureRuntime();
            Exception lastError = null;
            foreach (var token in tokens)
            {
                try
                {
                    onAttempt?.Invoke((OnlineQuality.FromId(token) ?? requested).Id);
                    var info = new JObject { ["type"] = token, ["musicInfo"] = SafeParse(track.MusicInfoJson) };
                    var raw = runtime.InvokeAsync("musicUrl", sourceId, info.ToString(Formatting.None), TimeSpan.FromSeconds(60))
                        .GetAwaiter().GetResult();
                    var url = ExtractUrl(raw);
                    if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("在线音源没有返回可播放地址");
                    return new ResolvedOnlineMedia
                    {
                        Url = url,
                        Quality = OnlineQuality.FromId(token) ?? requested,
                        SourceQuality = token,
                        MimeType = MimeTypeFromUrl(url)
                    };
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (!CanTryAnotherQuality(ex)) break;
                }
            }

            throw new InvalidOperationException(SourceError(lastError ?? new InvalidOperationException("未知错误")), lastError);
        }

        public static List<string> QualityTokensFor(OnlineQuality quality, List<string> supported,
            IEnumerable<OnlineQuality> trackAvailable = null)
        {
            var recognized = new List<KeyValuePair<string, OnlineQuality>>();
            foreach (var token in supported ?? new List<string>())
            {
                var parsed = OnlineQuality.FromId(token);
                if (parsed != null) recognized.Add(new KeyValuePair<string, OnlineQuality>(token, parsed));
            }
            if (recognized.Count == 0) return new List<string>();

            var available = new HashSet<OnlineQuality>((trackAvailable ?? Enumerable.Empty<OnlineQuality>()).Where(item => item != null));
            if (available.Count > 0)
            {
                var perTrack = recognized.Where(pair => available.Contains(pair.Value)).ToList();
                if (perTrack.Count > 0) recognized = perTrack;
            }

            var requestedRank = OnlineQuality.RankOf(quality);
            // Try the exact user choice first. If the track does not offer it,
            // or the source rejects it at runtime, fall back to the highest
            // available quality rather than silently dropping to a low tier.
            var ordered = recognized
                .OrderBy(pair => OnlineQuality.RankOf(pair.Value) == requestedRank ? 0 : 1)
                .ThenByDescending(pair => OnlineQuality.RankOf(pair.Value))
                .ToList();
            // Source aliases such as hires/flac24bit are the same quality.
            return ordered.GroupBy(pair => pair.Value).Select(group => group.First().Key).ToList();
        }

        private static bool CanTryAnotherQuality(Exception error)
        {
            var message = AllMessages(error).ToLowerInvariant();
            string[] fatal = { "鉴权", "订阅失效", "订阅过期", "401", "429", "请求过速", "过于频繁" };
            if (fatal.Any(message.Contains)) return false;
            string[] retry = { "音质不可用", "音质不支持", "不支持该音质", "没有该音质", "无此音质", "未获取到url", "没有返回可播放地址", "媒体服务器" };
            if (retry.Any(message.Contains)) return true;
            string[] stop = { "订阅", "403", "timeout", "超时" };
            return !stop.Any(message.Contains);
        }

        private static string AllMessages(Exception error)
        {
            var builder = new StringBuilder();
            for (var current = error; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message)) builder.Append(current.Message).Append(' ');
            }
            return builder.ToString();
        }

        public static string SourceError(Exception error)
        {
            var message = AllMessages(error).Trim();
            if (message.Contains("鉴权失败")) return "音源鉴权失败，请检查订阅";
            if (message.Contains("请求过速") || message.Contains("429")) return "请求过于频繁，请稍后重试";
            if (message.IndexOf("未获取到URL", StringComparison.OrdinalIgnoreCase) >= 0) return "当前歌曲所选音质不可用";
            if (message.Contains("在线音源请求超时")) return "在线音源请求超时，请稍后重试";
            var trimmed = System.Text.RegularExpressions.Regex.Replace(message, "https?://\\S+", "远程请求");
            trimmed = trimmed.Replace("\r", " ").Replace("\n", " ").Trim();
            var cut = trimmed.IndexOf('{');
            if (cut > 0) trimmed = trimmed.Substring(0, cut);
            trimmed = trimmed.Trim(' ', ',', ':', '：');
            if (trimmed.Length > 120) trimmed = trimmed.Substring(0, 120);
            return string.IsNullOrWhiteSpace(trimmed) ? "未知错误" : trimmed;
        }

        private static string HttpError(int status)
        {
            if (status == 401 || status == 403) return "在线音源鉴权失败，请检查订阅";
            if (status == 429) return "请求过于频繁，请稍后重试";
            return "在线服务返回 HTTP " + status;
        }

        public static string ExtractUrl(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JToken token;
            try { token = JToken.Parse(json); }
            catch { return json.Trim().Trim('"'); }
            return ExtractUrl(token);
        }

        private static string ExtractUrl(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.String:
                    var text = token.Value<string>();
                    return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                case JTokenType.Object:
                    foreach (var key in new[] { "url", "playUrl", "play_url", "data", "result" })
                    {
                        var child = token[key];
                        if (child == null || child.Type == JTokenType.Null) continue;
                        var found = ExtractUrl(child);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                    return null;
                case JTokenType.Array:
                    foreach (var child in token.Children())
                    {
                        var found = ExtractUrl(child);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                    return null;
                default:
                    return null;
            }
        }

        public static string MimeTypeFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "audio/mpeg";
            var path = url.Split('?')[0].Split('#')[0].ToLowerInvariant();
            if (path.EndsWith(".flac")) return "audio/flac";
            if (path.EndsWith(".m4a") || path.EndsWith(".mp4")) return "audio/mp4";
            if (path.EndsWith(".aac")) return "audio/aac";
            if (path.EndsWith(".ogg") || path.EndsWith(".opus")) return "audio/ogg";
            if (path.EndsWith(".wav")) return "audio/wav";
            if (path.EndsWith(".ape")) return "audio/ape";
            if (path.EndsWith(".m3u8")) return "application/x-mpegURL";
            return "audio/mpeg";
        }
        // ----------------------------------------------------------------- 曲目解析

        public static OnlineTrack ParseQqTrack(JObject item)
        {
            if (item == null) return null;

            // 搜索接口用 songmid/songname/albummid/albumname；
            // 歌单详情（new_format=1）用 mid/name/album.mid/album.name，必须同时兼容。
            var id = item.Value<string>("songmid");
            if (string.IsNullOrWhiteSpace(id)) id = item.Value<string>("mid");
            if (string.IsNullOrWhiteSpace(id)) id = item.Value<string>("strMediaMid");

            var title = TextUtil.HtmlDecode(item.Value<string>("songname"));
            if (string.IsNullOrWhiteSpace(title)) title = TextUtil.HtmlDecode(item.Value<string>("name"));
            if (string.IsNullOrWhiteSpace(title)) title = TextUtil.HtmlDecode(item.Value<string>("title"));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;

            var album = item["album"] as JObject;
            var file = item["file"] as JObject;

            var artists = NamesFromObjects(item["singer"] as JArray, "name");
            var albumMid = item.Value<string>("albummid");
            if (string.IsNullOrWhiteSpace(albumMid)) albumMid = album == null ? string.Empty : album.Value<string>("mid") ?? string.Empty;

            var qualities = new List<OnlineQuality>();
            var size128 = item.Value<long?>("size128") ?? (file == null ? 0L : file.Value<long?>("size_128mp3") ?? 0L);
            var size320 = item.Value<long?>("size320") ?? (file == null ? 0L : file.Value<long?>("size_320mp3") ?? 0L);
            var sizeFlac = item.Value<long?>("sizeflac") ?? (file == null ? 0L : file.Value<long?>("size_flac") ?? 0L);
            var sizeApe = item.Value<long?>("sizeape") ?? (file == null ? 0L : file.Value<long?>("size_ape") ?? 0L);
            if (size128 > 0 || size320 == 0) qualities.Add(OnlineQuality.Standard);
            if (size320 > 0) qualities.Add(OnlineQuality.High);
            if (sizeFlac > 0 || sizeApe > 0) qualities.Add(OnlineQuality.Lossless);
            if (qualities.Count == 0) qualities.Add(OnlineQuality.Standard);

            var types = new JObject();
            foreach (var quality in qualities) types[quality.Id] = new JObject();
            var artist = string.Join("、", artists);
            var albumName = TextUtil.HtmlDecode(item.Value<string>("albumname"));
            if (string.IsNullOrWhiteSpace(albumName) && album != null) albumName = TextUtil.HtmlDecode(album.Value<string>("name"));

            var mediaMid = item.Value<string>("strMediaMid");
            if (string.IsNullOrWhiteSpace(mediaMid)) mediaMid = item.Value<string>("media_mid");
            if (string.IsNullOrWhiteSpace(mediaMid) && file != null) mediaMid = file.Value<string>("media_mid");
            if (string.IsNullOrWhiteSpace(mediaMid)) mediaMid = string.Empty;

            var songId = item.Value<long?>("songid") ?? item.Value<long?>("id") ?? 0L;

            var payload = new JObject
            {
                ["source"] = "tx",
                ["time_public"] = item.Value<string>("time_public") ?? string.Empty,
                ["id"] = songId,
                ["songId"] = songId,
                ["songmid"] = id,
                ["strMediaMid"] = mediaMid,
                ["albumMid"] = albumMid,
                ["name"] = title,
                ["title"] = title,
                ["singer"] = artist,
                ["artist"] = artist,
                ["albumName"] = albumName,
                ["_types"] = types
            };

            return new OnlineTrack
            {
                Platform = OnlinePlatform.Qq,
                Id = id,
                Title = title,
                Artist = artist,
                Album = albumName,
                DurationMs = Math.Max(0, item.Value<long?>("interval") ?? 0) * 1000L,
                ArtworkUrl = string.IsNullOrWhiteSpace(albumMid)
                    ? string.Empty
                    : "https://y.gtimg.cn/music/photo_new/T002R500x500M000" + albumMid + ".jpg",
                AvailableQualities = qualities,
                MusicInfoJson = payload.ToString(Formatting.None)
            };
        }

        public static OnlineTrack ParseKugouTrack(JObject item)
        {
            if (item == null) return null;
            var audioId = AccountText(item, "Audioid", "audio_id", "audioid", "ID", "id");
            var baseHash = AccountText(item, "FileHash", "filehash", "file_hash", "hash");
            var id = string.IsNullOrWhiteSpace(audioId) ? baseHash : audioId;
            var fileName = AccountText(item, "FileName", "filename", "file_name");
            var rawTitle = AccountText(item, "SongName", "songname", "song_name", "name", "title");
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                var index = fileName.IndexOf(" - ", StringComparison.Ordinal);
                rawTitle = index >= 0 ? fileName.Substring(index + 3) : fileName;
            }
            var title = TextUtil.StripAudioFileExtension(TextUtil.HtmlDecode(rawTitle));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;

            var qualityData = new List<Tuple<OnlineQuality, long, string>>();
            AddQuality(qualityData, item, OnlineQuality.Standard, new[] { "FileSize", "filesize", "file_size" }, new[] { "FileHash", "filehash", "file_hash", "hash" });
            AddQuality(qualityData, item, OnlineQuality.High, new[] { "HQFileSize", "hqfilesize", "hq_file_size", "filesize_320" }, new[] { "HQFileHash", "hqfilehash", "hq_file_hash", "hash_320", "320hash" });
            AddQuality(qualityData, item, OnlineQuality.Lossless, new[] { "SQFileSize", "sqfilesize", "sq_file_size", "filesize_flac" }, new[] { "SQFileHash", "sqfilehash", "sq_file_hash", "hash_flac", "sqhash" });
            AddQuality(qualityData, item, OnlineQuality.HiRes, new[] { "ResFileSize", "resfilesize", "res_file_size", "filesize_high" }, new[] { "ResFileHash", "resfilehash", "res_file_hash", "hash_high" });
            if (qualityData.Count == 0 && !string.IsNullOrWhiteSpace(baseHash))
                qualityData.Add(Tuple.Create(OnlineQuality.Standard, 0L, baseHash));

            var qualities = qualityData.Select(x => x.Item1).ToList();
            if (qualities.Count == 0) qualities.Add(OnlineQuality.Standard);

            var types = new JObject();
            foreach (var entry in qualityData)
            {
                var sourceName = entry.Item1 == OnlineQuality.HiRes ? "flac24bit" : entry.Item1.Id;
                types[sourceName] = new JObject { ["size"] = entry.Item2, ["hash"] = entry.Item3 };
            }

            var artist = TextUtil.HtmlDecode(AccountText(item, "SingerName", "singername", "singer_name", "singer", "author_name", "author"));
            if (string.IsNullOrWhiteSpace(artist))
                artist = TextUtil.HtmlDecode(string.Join("、", NamesFromObjects(item["authors"] as JArray, "author_name")));
            if (string.IsNullOrWhiteSpace(artist))
            {
                var index = fileName.IndexOf(" - ", StringComparison.Ordinal);
                artist = index > 0 ? fileName.Substring(0, index) : string.Empty;
            }
            var album = TextUtil.HtmlDecode(AccountText(item, "AlbumName", "album_name", "albumname", "album", "remark"));

            var payload = new JObject
            {
                ["source"] = "kg",
                ["albumId"] = AccountText(item, "AlbumID", "album_id", "albumid"),
                ["id"] = id,
                ["songId"] = id,
                ["songmid"] = id,
                ["hash"] = baseHash,
                ["name"] = title,
                ["title"] = title,
                ["singer"] = artist,
                ["artist"] = artist,
                ["albumName"] = album,
                ["_types"] = types
            };

            var image = AccountText(item, "Image", "image", "AlbumImage", "albumimage", "album_image", "album_img");
            if (string.IsNullOrWhiteSpace(image)) image = (item["trans_param"] as JObject)?.Value<string>("union_cover") ?? string.Empty;
            image = image.Replace("{size}", "400").Replace("http://", "https://");

            var milliseconds = AccountLong(item, "timelength", "time_length");
            var durationValue = AccountLong(item, "Duration", "duration", "interval", "length");
            var durationMs = milliseconds > 0 ? milliseconds
                : (durationValue >= 1 && durationValue <= 9999 ? durationValue * 1000L : durationValue);

            return new OnlineTrack
            {
                Platform = OnlinePlatform.Kugou,
                Id = id,
                Title = title,
                Artist = artist,
                Album = album,
                DurationMs = Math.Max(0, durationMs),
                ArtworkUrl = image,
                AvailableQualities = qualities,
                MusicInfoJson = payload.ToString(Formatting.None)
            };
        }

        private static void AddQuality(List<Tuple<OnlineQuality, long, string>> target, JObject item,
            OnlineQuality quality, string[] sizeKeys, string[] hashKeys)
        {
            var size = AccountLong(item, sizeKeys);
            var hash = AccountText(item, hashKeys);
            if (size > 0 && !string.IsNullOrWhiteSpace(hash)) target.Add(Tuple.Create(quality, size, hash));
        }

        public static OnlineTrack ParseNeteaseTrack(JObject item)
        {
            if (item == null) return null;
            var id = item.Value<string>("id") ?? string.Empty;
            var title = (item.Value<string>("name") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;

            var albumJson = item["al"] as JObject ?? new JObject();
            var artists = NamesFromObjects(item["ar"] as JArray, "name");
            var qualities = new List<OnlineQuality>();
            if (item["l"] != null) qualities.Add(OnlineQuality.Standard);
            if (item["h"] != null) qualities.Add(OnlineQuality.High);
            if (item["sq"] != null) qualities.Add(OnlineQuality.Lossless);
            if (item["hr"] != null) qualities.Add(OnlineQuality.HiRes);
            if (qualities.Count == 0) qualities.Add(OnlineQuality.Standard);

            var types = new JObject();
            foreach (var quality in qualities) types[quality.Id] = new JObject();
            var artist = string.Join("、", artists);
            var album = albumJson.Value<string>("name") ?? string.Empty;
            var cover = albumJson.Value<string>("picUrl") ?? string.Empty;

            var payload = new JObject
            {
                ["source"] = "wy",
                ["albumId"] = albumJson.Value<string>("id") ?? string.Empty,
                ["publishTime"] = item.Value<long?>("publishTime") ?? 0,
                ["id"] = id,
                ["songId"] = id,
                ["songmid"] = id,
                ["name"] = title,
                ["title"] = title,
                ["singer"] = artist,
                ["artist"] = artist,
                ["albumName"] = album,
                ["img"] = cover,
                ["_types"] = types
            };

            return new OnlineTrack
            {
                Platform = OnlinePlatform.Netease,
                Id = id,
                Title = title,
                Artist = artist,
                Album = album,
                DurationMs = Math.Max(0, item.Value<long?>("dt") ?? 0),
                ArtworkUrl = cover.Replace("http://", "https://"),
                AvailableQualities = qualities,
                MusicInfoJson = payload.ToString(Formatting.None)
            };
        }

        private static List<string> NamesFromObjects(JArray array, string name)
        {
            var result = new List<string>();
            if (array == null) return result;
            foreach (var child in array.OfType<JObject>())
            {
                var value = TextUtil.HtmlDecode(child.Value<string>(name));
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            }
            return result;
        }

        internal static string AccountText(JObject item, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = item[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                var text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            return string.Empty;
        }

        internal static long AccountLong(JObject item, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = item[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<long>();
                if (token.Type == JTokenType.String && long.TryParse(token.Value<string>()?.Trim(), out var parsed)) return parsed;
            }
            return 0L;
        }
        // ----------------------------------------------------------------- LX 搜索结果解析

        private static readonly string[] TitleKeys = { "name", "title", "songname", "songName", "SongName", "musicName" };
        private static readonly string[] ArtistKeys = { "singer", "artist", "artists", "singers", "SingerName", "artistName" };
        private static readonly string[] AlbumKeys = { "albumName", "album", "AlbumName", "albumname" };
        private static readonly string[] ArtworkKeys = { "artworkUrl", "picUrl", "img", "pic", "cover", "albumPic", "album_img", "AlbumImage" };
        private static readonly string[] IdKeys = { "songmid", "songId", "id", "mid", "rid", "hash", "url_id", "copyrightId", "Audioid", "FileHash" };

        public static OnlineSearchPage ParseNativeSearchResult(string rawJson, OnlinePlatform platform, string source,
            int page, int limit, List<string> fallbackQualities)
        {
            var safePage = Math.Max(1, page);
            var safeLimit = Math.Max(1, Math.Min(50, limit));
            var root = ParseNativeJson(SafeParseToken(rawJson));
            var list = NativeTrackValues(root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tracks = new List<OnlineTrack>();
            foreach (var item in list)
            {
                var track = ParseNativeTrack(item, platform, source, fallbackQualities);
                if (track != null && seen.Add(track.Id)) tracks.Add(track);
            }

            var allPage = NativeInt(root, new[] { "allPage", "allPages", "maxPage", "pages" });
            var total = NativeInt(root, new[] { "total", "totalCount", "songCount", "count" });
            var isEnd = NativeBool(root, new[] { "isEnd", "end", "is_end" });
            var hasNext = NativeBool(root, new[] { "hasNext", "hasMore", "has_next", "more" });
            if (isEnd.HasValue) hasNext = !isEnd.Value;
            return new OnlineSearchPage { Tracks = tracks }
                .WithPagination(safePage, safeLimit, total, list.Count, hasNext, allPage);
        }

        private static JToken SafeParseToken(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return JValue.CreateNull();
            try { return JToken.Parse(json); }
            catch { return new JValue(json); }
        }

        public static OnlineTrack ParseNativeTrack(JToken value, OnlinePlatform platform, string source, List<string> fallbackQualities)
        {
            var raw = ParseNativeJson(value) as JObject;
            if (raw == null) return null;
            var item = (raw["musicInfo"] as JObject) ?? (raw["songInfo"] as JObject) ?? (raw["song"] as JObject) ?? raw;
            var id = NativeId(item, platform);
            if (string.IsNullOrWhiteSpace(id)) return null;
            var title = TextUtil.StripAudioFileExtension(NativeText(item, TitleKeys));
            if (string.IsNullOrWhiteSpace(title)) return null;
            var artist = NativeText(item, ArtistKeys);
            var album = NativeText(item, AlbumKeys);
            var qualityTokens = NativeQualityTokens(item);
            var qualities = qualityTokens.Concat(fallbackQualities ?? new List<string>())
                .Select(OnlineQuality.FromId).Where(q => q != null).Distinct().ToList();
            if (qualities.Count == 0) qualities.Add(OnlineQuality.Standard);

            var artwork = NativeText(item, ArtworkKeys);
            if (string.IsNullOrWhiteSpace(artwork)) artwork = InferredArtwork(item, platform);
            artwork = artwork.Replace("{size}", "500").Replace("http://", "https://");

            var payload = (JObject)item.DeepClone();
            PutIfBlank(payload, "source", source);
            PutIfBlank(payload, "id", id);
            PutIfBlank(payload, "songmid", id);
            PutIfBlank(payload, "songId", id);
            PutIfBlank(payload, "name", title);
            PutIfBlank(payload, "title", title);
            PutIfBlank(payload, "singer", artist);
            PutIfBlank(payload, "artist", artist);
            PutIfBlank(payload, "albumName", album);
            PutIfBlank(payload, "img", artwork);
            if (payload["_types"] == null && payload["types"] == null && qualityTokens.Count > 0)
            {
                var types = new JObject();
                foreach (var token in qualityTokens) types[token] = new JObject();
                payload["_types"] = types;
            }

            return new OnlineTrack
            {
                Platform = platform,
                Id = id,
                Title = title,
                Artist = artist,
                Album = album,
                DurationMs = NativeDurationMs(item),
                ArtworkUrl = artwork,
                AvailableQualities = qualities,
                MusicInfoJson = payload.ToString(Formatting.None)
            };
        }

        private static void PutIfBlank(JObject target, string key, string value)
        {
            var current = target[key];
            if (current == null || current.Type == JTokenType.Null || string.IsNullOrWhiteSpace(current.ToString())) target[key] = value;
        }

        private static JToken ParseNativeJson(JToken value)
        {
            var current = value;
            for (var i = 0; i < 4; i++)
            {
                if (current == null) return null;
                if (current.Type != JTokenType.String) return current;
                var text = current.Value<string>()?.Trim() ?? string.Empty;
                if (text.Length == 0 || (text[0] != '{' && text[0] != '[' && text[0] != '"')) return current;
                JToken parsed;
                try { parsed = JToken.Parse(text); }
                catch { return current; }
                if (JToken.DeepEquals(parsed, current)) return current;
                current = parsed;
            }
            return current;
        }

        private static List<JToken> NativeTrackValues(JToken value, int depth = 0)
        {
            var result = new List<JToken>();
            if (depth > 4) return result;
            var parsed = ParseNativeJson(value);
            if (parsed is JArray array) return array.Children().ToList();
            if (parsed is JObject obj)
            {
                foreach (var key in new[] { "list", "lists", "songs", "items", "records", "results" })
                {
                    var nested = NativeTrackValues(obj[key], depth + 1);
                    if (nested.Count > 0) return nested;
                }
                foreach (var key in new[] { "data", "result", "response", "body" })
                {
                    var nested = NativeTrackValues(obj[key], depth + 1);
                    if (nested.Count > 0) return nested;
                }
                if (NativeId(obj, OnlinePlatform.Qq) != null && !string.IsNullOrWhiteSpace(NativeText(obj, TitleKeys))) result.Add(obj);
            }
            return result;
        }

        private static int? NativeInt(JToken value, string[] keys, int depth = 0)
        {
            if (depth > 4) return null;
            var parsed = ParseNativeJson(value);
            if (parsed is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (!keys.Contains(property.Name)) continue;
                    var number = AsInt(property.Value);
                    if (number.HasValue) return number;
                }
                foreach (var key in new[] { "data", "result", "response", "body" })
                {
                    var nested = NativeInt(obj[key], keys, depth + 1);
                    if (nested.HasValue) return nested;
                }
            }
            else if (parsed is JArray array)
            {
                foreach (var child in array.Children())
                {
                    var nested = NativeInt(child, keys, depth + 1);
                    if (nested.HasValue) return nested;
                }
            }
            return null;
        }

        private static bool? NativeBool(JToken value, string[] keys, int depth = 0)
        {
            if (depth > 4) return null;
            var parsed = ParseNativeJson(value);
            if (parsed is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (!keys.Contains(property.Name)) continue;
                    switch (property.Value.Type)
                    {
                        case JTokenType.Boolean: return property.Value.Value<bool>();
                        case JTokenType.Integer: return property.Value.Value<long>() != 0;
                        case JTokenType.String:
                            var text = property.Value.Value<string>()?.Trim().ToLowerInvariant();
                            if (text == "true" || text == "1" || text == "yes") return true;
                            if (text == "false" || text == "0" || text == "no") return false;
                            break;
                    }
                }
                foreach (var key in new[] { "data", "result", "response", "body" })
                {
                    var nested = NativeBool(obj[key], keys, depth + 1);
                    if (nested.HasValue) return nested;
                }
            }
            else if (parsed is JArray array)
            {
                foreach (var child in array.Children())
                {
                    var nested = NativeBool(child, keys, depth + 1);
                    if (nested.HasValue) return nested;
                }
            }
            return null;
        }

        private static int? AsInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<int>();
            if (token.Type == JTokenType.String && double.TryParse(token.Value<string>()?.Trim(), out var parsed)) return (int)parsed;
            return null;
        }
        private static string NativeId(JObject item, OnlinePlatform platform)
        {
            string[] preferred;
            if (ReferenceEquals(platform, OnlinePlatform.Qq)) preferred = new[] { "songmid", "mid", "strMediaMid", "songId", "id" };
            else if (ReferenceEquals(platform, OnlinePlatform.Kugou)) preferred = new[] { "hash", "FileHash", "songmid", "Audioid", "id" };
            else preferred = new[] { "songmid", "id", "songId" };

            foreach (var key in preferred.Concat(IdKeys))
            {
                var text = NativeScalarText(item[key]).Trim();
                if (text.Length > 0 && text != "0") return text;
            }
            return null;
        }

        private static string NativeText(JObject item, string[] keys)
        {
            foreach (var key in keys)
            {
                var token = item[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                var text = TextUtil.HtmlDecode(NativeScalarText(token).Trim());
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return string.Empty;
        }

        private static string NativeScalarText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            if (token is JArray array)
            {
                var parts = array.Children().Select(NativeScalarText).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                return string.Join("、", parts);
            }
            if (token is JObject obj)
            {
                foreach (var key in new[] { "name", "title", "value", "url", "id" })
                {
                    var nested = NativeScalarText(obj[key]);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
                return string.Empty;
            }
            return token.ToString();
        }

        private static List<string> NativeQualityTokens(JObject item)
        {
            var result = new List<string>();
            foreach (var key in new[] { "_types", "types", "qualitys", "qualities", "availableQualities", "quality" })
            {
                var token = item[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                if (token is JObject obj)
                {
                    foreach (var property in obj.Properties()) result.Add(property.Name);
                }
                else if (token is JArray array)
                {
                    foreach (var child in array.Children())
                    {
                        var value = child is JObject childObject
                            ? NativeText(childObject, new[] { "type", "id", "quality", "name" })
                            : NativeScalarText(child);
                        if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
                    }
                }
                else if (token.Type == JTokenType.String)
                {
                    foreach (var part in token.Value<string>().Split(new[] { ',', '/', '|', '、', '&' }))
                    {
                        var trimmed = part.Trim();
                        if (trimmed.Length > 0) result.Add(trimmed);
                    }
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static long NativeDurationMs(JObject item)
        {
            var direct = AsLong(item["durationMs"]);
            if (direct.HasValue) return Math.Max(0, direct.Value);
            var dt = AsLong(item["dt"]);
            if (dt.HasValue) return Math.Max(0, dt.Value);
            foreach (var key in new[] { "interval", "_interval", "duration", "length" })
            {
                var token = item[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                var text = token.ToString().Trim();
                if (text.Contains(":"))
                {
                    var seconds = 0.0;
                    var valid = true;
                    foreach (var part in text.Split(':'))
                    {
                        if (!double.TryParse(part, out var value)) { valid = false; break; }
                        seconds = seconds * 60 + value;
                    }
                    if (valid) return (long)(seconds * 1000);
                    continue;
                }
                var number = AsLong(token);
                if (number.HasValue) return number.Value >= 10000 ? number.Value : number.Value * 1000L;
            }
            return 0L;
        }

        private static long? AsLong(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<long>();
            if (token.Type == JTokenType.String && double.TryParse(token.Value<string>()?.Trim(), out var parsed)) return (long)parsed;
            return null;
        }

        private static string InferredArtwork(JObject item, OnlinePlatform platform)
        {
            if (ReferenceEquals(platform, OnlinePlatform.Qq))
            {
                var albumMid = NativeText(item, new[] { "albumMid", "albummid" });
                if (string.IsNullOrWhiteSpace(albumMid)) albumMid = (item["album"] as JObject)?.Value<string>("mid") ?? string.Empty;
                return string.IsNullOrWhiteSpace(albumMid) ? string.Empty : "https://y.gtimg.cn/music/photo_new/T002R500x500M000" + albumMid + ".jpg";
            }
            if (ReferenceEquals(platform, OnlinePlatform.Kugou))
                return (item["trans_param"] as JObject)?.Value<string>("union_cover") ?? string.Empty;
            return (item["al"] as JObject)?.Value<string>("picUrl") ?? string.Empty;
        }

        public static JObject SafeParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try { return JObject.Parse(json); }
            catch { return new JObject(); }
        }

        // ----------------------------------------------------------------- 内置音源目录

        public List<KeyValuePair<string, string>> LoadBuiltInSources(string catalogUrl = null)
        {
            var url = string.IsNullOrWhiteSpace(catalogUrl) ? DefaultSourceCatalogUrl : catalogUrl.Trim();
            var response = OnlineHttp.Execute(url, timeoutMs: 8000, maxBytes: 128 * 1024);
            if (!response.Ok) throw new InvalidOperationException(HttpError(response.StatusCode));
            var urls = ParseBuiltInSourceCatalog(response.Body);
            var result = new List<KeyValuePair<string, string>>();
            for (var i = 0; i < urls.Count; i++) result.Add(new KeyValuePair<string, string>("内置音源 " + (i + 1), urls[i]));
            return result;
        }

        public static List<string> ParseBuiltInSourceCatalog(string text)
        {
            JToken root;
            try { root = JToken.Parse((text ?? string.Empty).TrimStart('\uFEFF')); }
            catch { throw new InvalidOperationException("内置音源列表格式不正确"); }
            var items = root as JArray ?? (root as JObject)?["sources"] as JArray;
            if (items == null) throw new InvalidOperationException("内置音源列表缺少 sources 数组");
            var result = new List<string>();
            foreach (var item in items)
            {
                string value = null;
                if (item.Type == JTokenType.String) value = item.Value<string>();
                else if (item is JObject obj) value = obj.Value<string>("url") ?? obj.Value<string>("script");
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (!result.Contains(value)) result.Add(value);
                if (result.Count >= 20) break;
            }
            if (result.Count == 0) throw new InvalidOperationException("内置音源列表为空");
            return result;
        }
    }
}
