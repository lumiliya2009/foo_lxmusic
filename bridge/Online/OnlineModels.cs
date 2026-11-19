﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace TzPlayer.Win.Online
{
    /// <summary>在线音乐平台。与 Android 版保持相同的 id / 别名。</summary>
    public sealed class OnlinePlatform
    {
        private readonly string[] _aliases;

        private OnlinePlatform(string id, string lxSourceId, string label, string[] aliases)
        {
            Id = id;
            LxSourceId = lxSourceId;
            Label = label;
            _aliases = aliases;
        }

        public string Id { get; }
        public string LxSourceId { get; }
        public string Label { get; }

        public static readonly OnlinePlatform Qq = new OnlinePlatform("qq", "tx", "QQ音乐", new[] { "tx", "qq", "tencent", "qqmusic" });
        public static readonly OnlinePlatform Kugou = new OnlinePlatform("kugou", "kg", "酷狗音乐", new[] { "kg", "kugou" });
        public static readonly OnlinePlatform Netease = new OnlinePlatform("netease", "wy", "网易云音乐", new[] { "wy", "netease", "163", "neteasecloud" });

        public static readonly OnlinePlatform[] All = { Qq, Kugou, Netease };

        public bool MatchesSourceId(string sourceId)
        {
            var raw = (sourceId ?? string.Empty).Trim().ToLowerInvariant();
            if (raw.Length == 0) return false;
            foreach (var alias in _aliases)
            {
                if (raw == alias) return true;
                if (TextUtil.NormalizeSourceId(raw) == TextUtil.NormalizeSourceId(alias)) return true;
            }
            return false;
        }

        public IEnumerable<string> SourceIdCandidates()
        {
            var list = new List<string>(_aliases) { LxSourceId, Id };
            return list.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static OnlinePlatform FromId(string id)
        {
            foreach (var platform in All)
            {
                if (platform.MatchesSourceId(id)) return platform;
            }
            return Qq;
        }

        public override string ToString() => Label;
    }

    /// <summary>音质档位，顺序即优先级。</summary>
    public sealed class OnlineQuality
    {
        private OnlineQuality(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }
        public string Label { get; }

        public static readonly OnlineQuality Standard = new OnlineQuality("128k", "128K");
        public static readonly OnlineQuality High = new OnlineQuality("320k", "320K");
        public static readonly OnlineQuality Lossless = new OnlineQuality("flac", "FLAC");
        public static readonly OnlineQuality HiRes = new OnlineQuality("hires", "Hi-Res");
        public static readonly OnlineQuality Premium = new OnlineQuality("atmos", "臻音");
        public static readonly OnlineQuality Master = new OnlineQuality("master", "母带");

        public static readonly OnlineQuality[] All = { Standard, High, Lossless, HiRes, Premium, Master };

        public static int RankOf(OnlineQuality quality)
        {
            for (var i = 0; i < All.Length; i++)
            {
                if (ReferenceEquals(All[i], quality)) return i;
            }
            return -1;
        }

        public static OnlineQuality FromId(string id)
        {
            switch (TextUtil.NormalizeQualityId(id))
            {
                case "128k": case "128": case "128kbps": case "low": case "standard": return Standard;
                case "320k": case "320": case "320kbps": case "high": case "exhigh": return High;
                case "flac": case "lossless": case "wav": case "ape": return Lossless;
                case "hires": case "hires24": case "hr": case "flac24bit": case "flac24":
                case "24bit": case "24": case "sky": return HiRes;
                case "atmos": case "dolby": case "dolbyatmos": case "surround": case "臻音": return Premium;
                case "master": case "jymaster": case "母带": return Master;
                default: return null;
            }
        }

        public override string ToString() => Label;
    }

    public sealed class OnlineTrack
    {
        public OnlinePlatform Platform { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public long DurationMs { get; set; }
        public string ArtworkUrl { get; set; }
        public List<OnlineQuality> AvailableQualities { get; set; } = new List<OnlineQuality>();
        public string MusicInfoJson { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Id : Title;
        public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知歌手" : Artist;
        public string DurationText => DurationMs <= 0 ? string.Empty :
            TimeSpan.FromMilliseconds(DurationMs).ToString(@"mm\:ss");
        public bool IsOnlineTrack => true;

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Artist) ? Title : Artist + " - " + Title;
    }

    public sealed class OnlineSearchPage
    {
        public List<OnlineTrack> Tracks { get; set; } = new List<OnlineTrack>();
        public int Page { get; set; } = 1;
        public bool HasNext { get; set; }
        public int Total { get; set; }
    }

    /// <summary>搜索类型：单曲 / 专辑 / 在线歌单 / 榜单 / 歌手。</summary>
    public sealed class OnlineSearchType
    {
        private OnlineSearchType(string id, string label, string queryHint)
        {
            Id = id;
            Label = label;
            QueryHint = queryHint;
        }

        public string Id { get; }
        public string Label { get; }
        public string QueryHint { get; }

        public static readonly OnlineSearchType Tracks = new OnlineSearchType("tracks", "单曲", "歌曲名或歌手");
        public static readonly OnlineSearchType Albums = new OnlineSearchType("albums", "专辑", "专辑名或歌手");
        public static readonly OnlineSearchType Playlists = new OnlineSearchType("playlists", "在线歌单", "在线歌单名称");
        public static readonly OnlineSearchType Rankings = new OnlineSearchType("rankings", "榜单", "不需要关键词，选中后直接浏览榜单");
        public static readonly OnlineSearchType Singers = new OnlineSearchType("singers", "歌手", "歌手名，留空则浏览热门歌手");

        /// <summary>搜索分类下拉里可选的类型：歌曲 / 专辑 / 歌单 / 歌手。</summary>
        public static readonly OnlineSearchType[] All = { Tracks, Albums, Playlists, Singers };

        /// <summary>榜单是独立的浏览入口，不放在搜索分类里。</summary>
        public bool IsBrowse => ReferenceEquals(this, Rankings);

        /// <summary>歌手可以留空（浏览热门歌手），其余类型都要关键词。</summary>
        public bool NeedsKeyword =>
            ReferenceEquals(this, Tracks) || ReferenceEquals(this, Albums) || ReferenceEquals(this, Playlists);

        public override string ToString() => Label;
    }

    /// <summary>专辑 / 歌单类型的搜索结果页。</summary>
    public sealed class OnlineCollectionPage
    {
        public List<OnlineCollection> Collections { get; set; } = new List<OnlineCollection>();
        public int Page { get; set; } = 1;
        public bool HasNext { get; set; }
        public int Total { get; set; }
    }

    public enum OnlineCollectionKind
    {
        Playlist,
        Album,
        Category,
        Ranking,
        Radio
    }

    public sealed class OnlineCollection
    {
        public OnlinePlatform Platform { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string ArtworkUrl { get; set; }
        public OnlineCollectionKind Kind { get; set; } = OnlineCollectionKind.Playlist;
        public int TrackCount { get; set; }
        public bool AccountScoped { get; set; }

        public override string ToString()
        {
            var details = (Subtitle ?? string.Empty).Trim();
            var count = TrackCount > 0 ? TrackCount + " 首" : string.Empty;
            var suffix = string.Join(" · ", new[] { details, count }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return string.IsNullOrWhiteSpace(suffix) ? Title : Title + "（" + suffix + "）";
        }
    }

    public sealed class ResolvedOnlineMedia
    {
        public string Url { get; set; }
        public OnlineQuality Quality { get; set; }
        public string SourceQuality { get; set; }
        public string MimeType { get; set; }
    }

    /// <summary>导入的 LX 音源描述信息。</summary>
    public sealed class LxSourceDescriptor
    {
        public string Name { get; set; } = "LX 在线音源";
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, List<string>> Sources { get; set; } = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> Actions { get; set; } = new Dictionary<string, List<string>>();

        public List<string> QualitiesFor(OnlinePlatform platform)
        {
            var match = MatchSource(platform);
            return match == null ? new List<string>() : match.Value.Value;
        }

        public List<string> ActionsFor(OnlinePlatform platform)
        {
            var id = MatchSourceId(platform);
            if (id == null) return new List<string>();
            if (Actions.TryGetValue(id, out var actions) && actions.Count > 0) return actions;
            return new List<string> { "musicUrl" };
        }

        public KeyValuePair<string, List<string>>? MatchSource(OnlinePlatform platform)
        {
            foreach (var candidate in platform.SourceIdCandidates())
            {
                foreach (var entry in Sources)
                {
                    if (platform.MatchesSourceId(entry.Key) &&
                        TextUtil.NormalizeSourceId(entry.Key) == TextUtil.NormalizeSourceId(candidate))
                    {
                        return entry;
                    }
                }
            }
            foreach (var entry in Sources)
            {
                if (platform.MatchesSourceId(entry.Key)) return entry;
            }
            return null;
        }

        public string MatchSourceId(OnlinePlatform platform)
        {
            var match = MatchSource(platform);
            return match?.Key;
        }

        public bool Supports(OnlinePlatform platform) => MatchSource(platform) != null;
    }

    public sealed class StoredOnlineSource
    {
        [JsonProperty("url")] public string Url { get; set; } = string.Empty;
        [JsonProperty("script")] public string Script { get; set; } = string.Empty;
        [JsonProperty("displayName")] public string DisplayName { get; set; } = string.Empty;
        [JsonProperty("descriptor")] public LxSourceDescriptor Descriptor { get; set; } = new LxSourceDescriptor();
    }

    public sealed class OnlineSourceState
    {
        public string Name { get; set; } = "未导入在线音源";
        public string Version { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public bool Imported { get; set; }
        public LxSourceDescriptor Descriptor { get; set; } = new LxSourceDescriptor();
        public OnlinePlatform DefaultPlatform { get; set; } = OnlinePlatform.Qq;
        public OnlineQuality PreferredQuality { get; set; } = OnlineQuality.High;
    }

    public sealed class OnlineComment
    {
        public string Id { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public long TimeMs { get; set; }
        public string TimeLabel { get; set; } = string.Empty;
        public int LikedCount { get; set; }
        public int ReplyCount { get; set; }
        public string RepliedTo { get; set; } = string.Empty;
        public List<OnlineComment> Children { get; set; } = new List<OnlineComment>();
        public bool LikedByMe { get; set; }
    }

    public sealed class OnlineCommentPage
    {
        public int Total { get; set; }
        public List<OnlineComment> Comments { get; set; } = new List<OnlineComment>();
        public int Page { get; set; } = 1;
        public bool HasNext { get; set; }
    }

    public sealed class OnlineAccountSummary
    {
        public OnlinePlatform Platform { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long AuthorizedAt { get; set; }

        public override string ToString() =>
            Platform.Label + "：" + (string.IsNullOrWhiteSpace(DisplayName) ? UserId : DisplayName);
    }

    public sealed class StoredOnlineAccount
    {
        public OnlineAccountSummary Summary { get; set; }
        public string Cookie { get; set; } = string.Empty;
    }
}
