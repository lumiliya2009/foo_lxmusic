using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    /// <summary>歌单导入：公开分享链接、QQ 号、授权账号歌单。</summary>
    public sealed class OnlinePlaylistService
    {
        private readonly OnlineMusicRepository _repository;

        public OnlinePlaylistService(OnlineMusicRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>解析输入文本（QQ号或分享链接），返回可导入的公开歌单。</summary>
        public List<OnlineCollection> LookupInput(string input)
        {
            var clean = (input ?? string.Empty).Trim();
            if (clean.Length == 0) throw new InvalidOperationException("请输入 QQ号或公开歌单链接");

            var qqNumber = OnlinePlaylistInputParser.QqNumber(clean);
            if (qqNumber != null) return LookupQqCreatedPlaylists(qqNumber);

            var urls = OnlinePlaylistInputParser.ExtractUrls(clean);
            if (urls.Count == 0) throw new InvalidOperationException("请输入 QQ号或公开歌单链接");

            var targets = new List<OnlinePlaylistTarget>();
            foreach (var url in urls)
            {
                OnlinePlaylistTarget target = null;
                try { target = OnlinePlaylistInputParser.ParseUrl(url) ?? ResolveLink(url); }
                catch { target = null; }
                if (target == null) continue;
                if (targets.Any(t => t.Platform.Id == target.Platform.Id && t.Id == target.Id)) continue;
                targets.Add(target);
            }
            if (targets.Count == 0)
                throw new InvalidOperationException("没有识别到可导入的公开歌单，请确认链接完整（支持 QQ音乐 / 酷狗 / 网易云歌单分享链接或短链）");

            var result = new List<OnlineCollection>();
            var problems = new List<string>();
            foreach (var target in targets)
            {
                try { result.Add(Metadata(target)); }
                catch (Exception ex) { problems.Add(target.Platform.Label + "：" + ex.Message); }
            }
            if (result.Count == 0)
                throw new InvalidOperationException(problems.Count > 0 ? string.Join("；", problems) : "没有识别到可导入的公开歌单");
            return result;
        }

        public OnlineCollection Metadata(OnlinePlaylistTarget target)
        {
            if (ReferenceEquals(target.Platform, OnlinePlatform.Kugou)) return KugouPlaylistApi.Metadata(target.Id);
            if (ReferenceEquals(target.Platform, OnlinePlatform.Netease)) return NeteasePlaylistLoader.Metadata(target.Id);
            return QqPlaylistMetadata(target.Id);
        }

        public OnlineSearchPage Tracks(OnlineCollection collection, int page)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (collection.Kind == OnlineCollectionKind.Album)
                return OnlineCollectionSearch.AlbumTracks(collection, page);
            if (collection.Kind == OnlineCollectionKind.Ranking)
                return OnlineCatalogBrowser.RankingTracks(collection, page);
            if (collection.Kind == OnlineCollectionKind.Category)
                throw new InvalidOperationException("请先选择分类中的歌单");
            var cookie = _repository.Accounts.Account(collection.Platform)?.Cookie ?? string.Empty;
            OnlineSearchPage result;
            if (ReferenceEquals(collection.Platform, OnlinePlatform.Kugou))
                result = KugouPlaylistApi.Tracks(collection, page);
            else if (ReferenceEquals(collection.Platform, OnlinePlatform.Netease))
                result = NeteasePlaylistLoader.Tracks(collection.Id, page, cookie);
            else if (ReferenceEquals(collection.Platform, OnlinePlatform.Kuwo))
                result = OnlineCatalogBrowser.KuwoPlaylistTracks(collection, page);
            else
                result = QqPlaylistTracks(collection, cookie, page);
            if (collection.Kind != OnlineCollectionKind.Ranking)
                result.Tracks = result.Tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        /// <summary>读取已授权账号的歌单（QQ / 网易云）。</summary>
        public List<OnlineCollection> AccountPlaylists(OnlinePlatform platform)
        {
            var account = _repository.Accounts.Account(platform);
            if (account == null) throw new InvalidOperationException("请先在“账号授权”中授权" + platform.Label + "账号");
            if (ReferenceEquals(platform, OnlinePlatform.Netease))
                return NeteasePlaylistLoader.UserPlaylists(account.Summary.UserId, account.Cookie);
            if (ReferenceEquals(platform, OnlinePlatform.Qq))
            {
                var cookies = OnlineAccountCookies.Parse(account.Cookie);
                var userId = account.Summary.UserId;
                if (string.IsNullOrWhiteSpace(userId))
                    userId = new string(OnlineAccountCookies.Value(cookies, "uin", "qqmusic_uin", "wxuin", "p_uin")
                        .Where(char.IsDigit).ToArray()).TrimStart('0');
                if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("QQ音乐账号信息不完整，请重新授权");
                return LookupQqCreatedPlaylists(userId, account.Summary.DisplayName, true)
                    .GroupBy(c => c.Id).Select(g => g.First()).ToList();
            }
            throw new InvalidOperationException("酷狗音乐暂不支持读取授权账号歌单，请改用歌单链接导入");
        }

        private OnlinePlaylistTarget ResolveLink(string url)
        {
            var response = OnlineHttp.Execute(url, headers: new Dictionary<string, string>
            {
                ["Referer"] = "https://music.163.com/",
                ["User-Agent"] = OnlineHttp.BrowserUserAgent
            }, maxBytes: 512 * 1024);
            if (!response.Ok) throw new InvalidOperationException("在线服务返回 HTTP " + response.StatusCode);
            var candidates = new List<string> { response.FinalUrl };
            candidates.AddRange(OnlinePlaylistInputParser.ExtractUrls(response.Body));
            foreach (var candidate in candidates)
            {
                var target = OnlinePlaylistInputParser.ParseUrl(candidate);
                if (target != null) return target;
            }
            throw new InvalidOperationException("无法从链接中识别公开歌单，请确认链接完整");
        }

        /// <summary>
        /// 读取 QQ 号公开的自建歌单（<c>fcg_user_created_diss</c>）。
        /// 腾讯已停止匿名返回歌单列表；拿不到时给出可操作的提示，而不是笼统报错。
        /// </summary>
        private List<OnlineCollection> LookupQqCreatedPlaylists(string qqNumber, string ownerOverride = null, bool accountScoped = false)
        {
            // 注意：这个接口不能带 ct / cid / reqtype 参数，否则腾讯会返回空的 disslist。
            const int pageSize = 100;
            var result = new List<OnlineCollection>();
            var owner = ownerOverride;
            var total = 0;
            for (var offset = 0; offset < 1000; offset += pageSize)
            {
                var url = OnlineHttp.Query("https://c.y.qq.com/rsc/fcgi-bin/fcg_user_created_diss",
                    new Dictionary<string, object>
                    {
                        ["hostuin"] = qqNumber,
                        ["sin"] = offset,
                        ["size"] = pageSize,
                        ["format"] = "json",
                        ["g_tk"] = 5381,
                        ["inCharset"] = "utf-8",
                        ["outCharset"] = "utf-8",
                        ["notice"] = 0,
                        ["platform"] = "yqq.json",
                        ["needNewCode"] = 0
                    });
                var response = OnlineHttp.Execute(url, headers: QqHeaders(string.Empty, "https://y.qq.com/portal/profile.html"),
                    maxBytes: 4 * 1024 * 1024);
                if (!response.Ok) throw new InvalidOperationException("在线服务返回 HTTP " + response.StatusCode);
                var root = OnlineMusicRepository.SafeParse(response.Body);
                if (root.Value<int?>("code") != 0)
                    throw new InvalidOperationException(accountScoped ? "QQ音乐账号歌单读取失败" : "QQ音乐没有返回这个账号的公开歌单");

                var data = root["data"] as JObject ?? new JObject();
                if (string.IsNullOrWhiteSpace(owner))
                {
                    owner = data.Value<string>("hostname");
                    if (string.IsNullOrWhiteSpace(owner)) owner = data.Value<string>("nickname");
                }
                total = Math.Max(total, data.Value<int?>("totoal") ?? data.Value<int?>("total") ?? 0);
                var list = data["disslist"] as JArray ?? data["cdlist"] as JArray ?? new JArray();
                foreach (var item in list.OfType<JObject>())
                {
                    var collection = QqPlaylistCollection(item, owner, accountScoped);
                    if (collection != null && result.All(c => c.Id != collection.Id)) result.Add(collection);
                }
                if (list.Count < pageSize) break;
            }

            if (result.Count > 0) return result;
            var who = string.IsNullOrWhiteSpace(owner) ? "这个 QQ号" : owner;
            if (accountScoped)
                throw new InvalidOperationException("QQ音乐账号歌单读取失败：请重新授权账号，或改用歌单分享链接导入");
            if (total > 0)
                throw new InvalidOperationException(who + " 的歌单都没有公开（" + total + " 个）。" +
                    "请在 QQ音乐里把歌单设为公开，或打开歌单 → 分享 → 复制链接后粘贴到这里导入。");
            throw new InvalidOperationException(who + " 没有公开的歌单。可在 QQ音乐中把歌单设为公开，或直接粘贴歌单分享链接。");
        }

        private static OnlineCollection QqPlaylistCollection(JObject item, string owner, bool accountScoped)
        {
            if (item == null) return null;
            var rawId = item.Value<string>("tid");
            if (string.IsNullOrWhiteSpace(rawId)) rawId = item.Value<string>("disstid");
            if (string.IsNullOrWhiteSpace(rawId)) rawId = item.Value<string>("dissid");
            if (string.IsNullOrWhiteSpace(rawId)) rawId = item.Value<string>("id");
            var title = TextUtil.HtmlDecode(item.Value<string>("diss_name"));
            if (string.IsNullOrWhiteSpace(title)) title = TextUtil.HtmlDecode(item.Value<string>("dissname"));
            if (string.IsNullOrWhiteSpace(title)) title = TextUtil.HtmlDecode(item.Value<string>("name"));
            if (string.IsNullOrWhiteSpace(rawId) || rawId == "0" || string.IsNullOrWhiteSpace(title)) return null;
            var dirShow = item.Value<int?>("dir_show");
            if (dirShow.HasValue && dirShow.Value == 0) return null;

            var count = item.Value<int?>("song_cnt") ?? item.Value<int?>("songnum") ?? item.Value<int?>("song_count") ?? 0;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(owner)) parts.Add(owner);
            if (count > 0) parts.Add(count + " 首");
            var cover = item.Value<string>("diss_cover");
            if (string.IsNullOrWhiteSpace(cover)) cover = item.Value<string>("cover");
            if (string.IsNullOrWhiteSpace(cover)) cover = item.Value<string>("logo");

            return new OnlineCollection
            {
                Platform = OnlinePlatform.Qq,
                Id = accountScoped ? "account:" + rawId : rawId,
                Title = title,
                Subtitle = string.Join(" · ", parts),
                ArtworkUrl = (cover ?? string.Empty).Replace("http://", "https://"),
                Kind = OnlineCollectionKind.Playlist,
                TrackCount = count,
                AccountScoped = accountScoped
            };
        }

        private static OnlineCollection QqPlaylistMetadata(string id)
        {
            var detail = QqPlaylistDetail(id, string.Empty, 1);
            var title = TextUtil.HtmlDecode(detail.Value<string>("dissname"));
            if (string.IsNullOrWhiteSpace(title)) title = "QQ音乐歌单 " + id;
            var count = detail.Value<int?>("songnum") ?? detail.Value<int?>("song_cnt") ?? 0;
            var owner = TextUtil.HtmlDecode(detail.Value<string>("nickname"));
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(owner)) parts.Add(owner);
            if (count > 0) parts.Add(count + " 首");
            return new OnlineCollection
            {
                Platform = OnlinePlatform.Qq,
                Id = id,
                Title = title,
                Subtitle = string.Join(" · ", parts),
                ArtworkUrl = (detail.Value<string>("logo") ?? string.Empty).Replace("http://", "https://"),
                Kind = OnlineCollectionKind.Playlist,
                TrackCount = count
            };
        }

        private static OnlineSearchPage QqPlaylistTracks(OnlineCollection collection, string cookie, int page)
        {
            var safePage = Math.Max(1, page);
            var id = collection.Id.StartsWith("account:", StringComparison.Ordinal)
                ? collection.Id.Substring("account:".Length)
                : collection.Id;
            var detail = QqPlaylistDetail(id, cookie, safePage);
            var raw = detail["songlist"] as JArray ?? new JArray();
            var tracks = raw.OfType<JObject>().Select(OnlineMusicRepository.ParseQqTrack).Where(t => t != null).ToList();
            var total = Math.Max(Math.Max(collection.TrackCount, detail.Value<int?>("songnum") ?? 0), tracks.Count);
            return new OnlineSearchPage
            {
                Tracks = tracks,
                Page = safePage,
                HasNext = total > 0 ? safePage * 100 < total : raw.Count >= 100,
                Total = total
            };
        }

        private static JObject QqPlaylistDetail(string id, string cookie, int page)
        {
            var url = OnlineHttp.Query("https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg",
                new Dictionary<string, object>
                {
                    ["type"] = 1, ["json"] = 1, ["utf8"] = 1, ["onlysong"] = 0, ["new_format"] = 1,
                    ["disstid"] = id, ["song_begin"] = (Math.Max(1, page) - 1) * 100, ["song_num"] = 100,
                    ["format"] = "json", ["platform"] = "yqq.json"
                });
            var response = OnlineHttp.Execute(url, headers: QqHeaders(cookie, "https://y.qq.com/"), maxBytes: 8 * 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException("QQ音乐歌单读取失败");
            var root = OnlineMusicRepository.SafeParse(response.Body);
            if (root.Value<int?>("code") != 0) throw new InvalidOperationException("QQ音乐歌单读取失败");
            return (root["cdlist"] as JArray)?.OfType<JObject>().FirstOrDefault() ?? new JObject();
        }

        private static Dictionary<string, string> QqHeaders(string cookie, string referer)
        {
            var headers = new Dictionary<string, string>
            {
                ["Referer"] = referer,
                ["User-Agent"] = OnlineHttp.BrowserUserAgent
            };
            if (!string.IsNullOrWhiteSpace(cookie)) headers["Cookie"] = cookie;
            return headers;
        }

        private static int QqGtk(string cookie)
        {
            var key = OnlineAccountCookies.Value(OnlineAccountCookies.Parse(cookie ?? string.Empty), "p_skey", "skey");
            if (string.IsNullOrWhiteSpace(key)) return 5381;
            var hash = 5381;
            foreach (var ch in key) hash += (hash << 5) + ch;
            return hash & 0x7fffffff;
        }
    }
}
