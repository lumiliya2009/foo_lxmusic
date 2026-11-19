using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    internal static class Program
    {
        private const int DefaultLimit = 30;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.Length < 2) throw new InvalidOperationException("缺少命令或数据目录");
                var command = args[0].Trim().ToLowerInvariant();
                AppPaths.Initialize(PayloadCodec.DecodeText(args[1]));

                var store = new OnlineSourceStore();
                var accounts = new OnlineAccountStore();
                using (var repository = new RepositoryScope(new OnlineMusicRepository(store, accounts)))
                {
                    switch (command)
                    {
                        case "status": Status(store); break;
                        case "source-options": SourceOptions(store); break;
                        case "select-source": store.Select(args.Length > 2 ? args[2] : string.Empty); Status(store); break;
                        case "check-update": CheckUpdate(args); break;
                        case "import-file": ImportFile(repository.Value, ArgText(args, 2)); break;
                        case "import-url": ImportUrl(repository.Value, ArgText(args, 2)); break;
                        case "remove-source": repository.Value.ResetSource(args.Length > 2 ? args[2] : null); Ok("已删除音源"); break;
                        case "search": Search(repository.Value, args); break;
                        case "expand": Expand(repository.Value, args); break;
                        case "resolve": Resolve(repository.Value, args); break;
                        case "lyrics": Lyrics(args); break;
                        case "download-batch": DownloadBatch(repository.Value, args); break;
                        default: throw new InvalidOperationException("未知命令：" + command);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Emit("ERROR", PayloadCodec.EncodeText(UserMessage(ex)));
                return 1;
            }
        }

        private static void Status(OnlineSourceStore store)
        {
            var state = store.State;
            if (!state.Imported)
            {
                Ok("尚未导入 LX 音源");
                return;
            }
            var sites = string.Join("、", state.Descriptor.Sources.Keys.Select(PlatformLabel));
            Ok(state.Name + (string.IsNullOrWhiteSpace(state.Version) ? "" : " " + state.Version) +
               (sites.Length == 0 ? "" : "（" + sites + "）"));
        }

        private static void SourceOptions(OnlineSourceStore store)
        {
            var active = store.State.Id;
            foreach (var source in store.List())
            {
                Emit("SOURCE", source.Id, PayloadCodec.EncodeText(source.Name), source.Id == active ? "1" : "0");
                var seen = new HashSet<string>();
                foreach (var key in source.Descriptor.Sources.Keys)
                {
                    var platform = OnlinePlatform.FromId(key);
                    if (!seen.Add(platform.Id)) continue;
                    var label = platform.Label;
                    if (label == platform.Id && source.Descriptor.Names.TryGetValue(key, out var sourceName) && !string.IsNullOrWhiteSpace(sourceName)) label = sourceName;
                    var searchable = platform.HasBuiltInSearch || source.Descriptor.ActionsFor(platform).Any(action =>
                        string.Equals(action, "search", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "musicSearch", StringComparison.OrdinalIgnoreCase));
                    if (!searchable) continue;
                    Emit("SITE", source.Id, platform.Id, PayloadCodec.EncodeText(label),
                        platform.SupportsCollections ? "track,album,artist,playlist" : "track");
                }
            }
        }

        private static void CheckUpdate(string[] args)
        {
            if (args.Length < 3) throw new InvalidOperationException("缺少当前版本号");
            var update = UpdateChecker.Check(args[2]);
            if (update == null) Ok("当前已是最新版本（" + args[2] + "）");
            else Emit("UPDATE", PayloadCodec.EncodeText(update.Url), update.Version);
        }

        private static void ImportFile(OnlineMusicRepository repository, string path)
        {
            var descriptor = repository.ImportLocalFile(path, Path.GetFileNameWithoutExtension(path));
            Ok(DescriptorText(descriptor));
        }

        private static void ImportUrl(OnlineMusicRepository repository, string url)
        {
            var descriptor = repository.ImportSource(url, string.Empty);
            Ok(DescriptorText(descriptor));
        }

        private static string DescriptorText(LxSourceDescriptor descriptor)
        {
            var sites = string.Join("、", descriptor.Sources.Keys.Select(PlatformLabel));
            return "音源已导入：" + descriptor.Name +
                   (string.IsNullOrWhiteSpace(descriptor.Version) ? "" : " " + descriptor.Version) +
                   (sites.Length == 0 ? "" : "；支持 " + sites);
        }

        private static void Search(OnlineMusicRepository repository, string[] args)
        {
            if (args.Length < 8) throw new InvalidOperationException("搜索参数不完整");
            var platform = OnlinePlatform.FromId(args[2]);
            var type = args[3].ToLowerInvariant();
            var query = PayloadCodec.DecodeText(args[4]).Trim();
            var page = PositiveInt(args[5], 1);
            var limit = Math.Min(50, PositiveInt(args[6], DefaultLimit));
            var quality = args[7];
            if (args.Length > 8 && !string.IsNullOrEmpty(args[8]))
            {
                repository.Sources.Select(args[8]);
                repository.Sources.Use(args[8]);
            }
            if (repository.Sources.State.Imported && !repository.Sources.State.Descriptor.Supports(platform))
                throw new InvalidOperationException("当前音源不支持" + platform.Label);
            if (type != "track" && !platform.SupportsCollections)
                throw new InvalidOperationException(platform.Label + "目前支持单曲搜索");

            if (type == "track")
            {
                foreach (var track in repository.Search(platform, query, page, limit).Tracks)
                    Emit("ITEM", PayloadCodec.FromTrack(track, quality));
            }
            else if (type == "album" || type == "playlist")
            {
                var searchType = type == "album" ? OnlineSearchType.Albums : OnlineSearchType.Playlists;
                foreach (var collection in OnlineCollectionSearch.Search(platform, searchType, query, page, limit).Collections)
                    Emit("ITEM", PayloadCodec.FromCollection(collection, type, quality));
            }
            else if (type == "artist")
            {
                foreach (var collection in ArtistCatalog.Search(platform, query, page, limit))
                    Emit("ITEM", PayloadCodec.FromCollection(collection, "artist", quality));
            }
            else throw new InvalidOperationException("不支持的搜索类型：" + type);
            Ok("搜索完成");
        }

        private static void Expand(OnlineMusicRepository repository, string[] args)
        {
            if (args.Length < 4) throw new InvalidOperationException("展开参数不完整");
            var payload = PayloadCodec.Parse(args[2]);
            var quality = args[3];
            OnlineSearchPage page;
            var collection = payload.ToCollection();
            if (payload.Kind == "album")
                page = OnlineCollectionSearch.AlbumTracks(collection, 1);
            else if (payload.Kind == "playlist")
                page = new OnlinePlaylistService(repository).Tracks(collection, 1);
            else if (payload.Kind == "artist")
                page = ArtistCatalog.Tracks(collection, 1, 100);
            else throw new InvalidOperationException("所选项目不是专辑、歌手或歌单");

            foreach (var track in page.Tracks)
                Emit("ITEM", PayloadCodec.FromTrack(track, quality));
            Ok("已展开 " + page.Tracks.Count + " 首歌曲");
        }

        private static void Resolve(OnlineMusicRepository repository, string[] args)
        {
            if (args.Length < 4) throw new InvalidOperationException("解析参数不完整");
            var payload = PayloadCodec.Parse(args[2]);
            if (payload.Kind != "track") throw new InvalidOperationException("请先按回车打开专辑、歌手或歌单，再选择歌曲播放");
            var wanted = OnlineQuality.FromId(args[3]) ?? OnlineQuality.FromId(payload.PreferredQuality) ?? OnlineQuality.High;
            if (args.Length > 5 && !string.IsNullOrEmpty(args[5])) repository.Sources.Use(args[5]);
            Emit("SOURCEID", repository.Sources.State.Id);
            var tried = new List<string>(args.Length > 4 ? args[4].Split(',') : Array.Empty<string>());
            var track = payload.ToTrack();
            var media = repository.Resolve(track, wanted, tried, quality =>
            {
                tried.Add(quality);
                Emit("TRIED", quality);
            });
            Emit("URL", PayloadCodec.EncodeText(media.Url), media.Quality?.Id ?? wanted.Id,
                PayloadCodec.EncodeText(media.MimeType ?? string.Empty));
            Emit("REMAINING", repository.RemainingQualityTokens(track, wanted, tried).Count.ToString(CultureInfo.InvariantCulture));
        }

        private static void Lyrics(string[] args)
        {
            if (args.Length < 3) throw new InvalidOperationException("歌词参数不完整");
            var track = PayloadCodec.Parse(args[2]).ToTrack();
            var cacheDirectory = AppPaths.File("lyrics-cache");
            Directory.CreateDirectory(cacheDirectory);
            var cacheKey = TextUtil.Md5Hex((track.Title ?? "") + "\n" + (track.Artist ?? "") + "\n" +
                (track.DurationMs / 1000).ToString(CultureInfo.InvariantCulture));
            var cachePath = Path.Combine(cacheDirectory, cacheKey + ".lrc");
            if (File.Exists(cachePath))
            {
                var cached = File.ReadAllText(cachePath, Encoding.UTF8);
                if (LyricsProviderClient.IsTimed(cached))
                {
                    Emit("LYRICS", PayloadCodec.EncodeText(cached));
                    return;
                }
            }
            var lyric = new LyricsProviderClient(CancellationToken.None).Fetch(track);
            if (string.IsNullOrWhiteSpace(lyric)) throw new InvalidOperationException("没有找到匹配的同步歌词");
            File.WriteAllText(cachePath, lyric, new UTF8Encoding(false));
            Emit("LYRICS", PayloadCodec.EncodeText(lyric));
        }

        private static void DownloadBatch(OnlineMusicRepository repository, string[] args)
        {
            if (args.Length < 5) throw new InvalidOperationException("下载参数不完整");
            var listPath = PayloadCodec.DecodeText(args[2]);
            var outputDirectory = PayloadCodec.DecodeText(args[3]);
            var quality = OnlineQuality.FromId(args[4]) ?? OnlineQuality.High;
            var payloads = File.ReadAllLines(listPath, Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            if (payloads.Count == 0) throw new InvalidOperationException("没有可下载的歌曲或专辑");

            var jobs = new List<DownloadJob>();
            var albums = new List<OnlineAlbumDownload>();
            var errors = new List<string>();
            var warnings = new List<string>();
            var failedItems = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var encoded in payloads)
            {
                BridgePayload payload = null;
                try
                {
                    payload = PayloadCodec.Parse(encoded);
                    if (!seen.Add(payload.Kind + ":" + payload.PlatformId + ":" + payload.Id)) continue;
                    if (payload.Kind == "track") jobs.Add(new DownloadJob { Track = payload.ToTrack(), Directory = outputDirectory });
                    else if (payload.Kind == "album")
                    {
                        Emit("STATUS", PayloadCodec.EncodeText("正在读取整张专辑：" + payload.Title));
                        var album = OnlineAlbumDownload.Prepare(payload.ToCollection(), outputDirectory, OnlineCollectionSearch.AlbumTracks);
                        albums.Add(album);
                        for (var i = 0; i < album.Tracks.Count; i++)
                            jobs.Add(new DownloadJob { Track = album.Tracks[i], Directory = album.DirectoryPath, Album = album, AlbumIndex = i });
                    }
                    else throw new InvalidOperationException("仅支持下载歌曲和专辑");
                }
                catch (Exception ex)
                {
                    failedItems++;
                    errors.Add((payload == null ? "下载项目" : payload.Title) + "：" + UserMessage(ex));
                }
            }

            var covers = 0;
            foreach (var album in albums)
            {
                Emit("STATUS", PayloadCodec.EncodeText("正在保存专辑封面：" + album.Album.Title));
                try { album.SaveArtwork(); covers++; }
                catch (Exception ex) { warnings.Add(album.Album.Title + "：" + UserMessage(ex)); }
            }
            var ok = 0;
            var failedTracks = 0;
            for (var index = 0; index < jobs.Count; index++)
            {
                var job = jobs[index];
                var track = job.Track;
                try
                {
                    var media = repository.Resolve(track, quality);
                    var wantedName = job.Album == null ? OnlineDownloader.BuildFileName(track, media) : job.Album.TrackFileName(job.AlbumIndex, media);
                    var fileName = UniqueFileName(job.Directory, wantedName);
                    var lastReport = DateTime.MinValue;
                    Emit("STATUS", PayloadCodec.EncodeText("正在下载 " + (index + 1) + "/" + jobs.Count + "：" + track));
                    OnlineDownloader.Download(media.Url, job.Directory, fileName, (received, total) =>
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds < 100 && (total <= 0 || received < total)) return;
                        lastReport = now;
                        Emit("TRANSFER", (index + 1).ToString(CultureInfo.InvariantCulture),
                            jobs.Count.ToString(CultureInfo.InvariantCulture),
                            received.ToString(CultureInfo.InvariantCulture),
                            Math.Max(0, total).ToString(CultureInfo.InvariantCulture),
                            PayloadCodec.EncodeText(track.ToString()));
                    }, CancellationToken.None);
                    ok++;
                }
                catch (Exception ex)
                {
                    failedTracks++;
                    errors.Add((track == null ? "第 " + (index + 1) + " 首" : track.ToString()) + "：" + UserMessage(ex));
                }
                Emit("PROGRESS", (index + 1).ToString(CultureInfo.InvariantCulture),
                    jobs.Count.ToString(CultureInfo.InvariantCulture),
                    PayloadCodec.EncodeText(track == null ? "第 " + (index + 1) + " 首" : track.ToString()));
            }
            var summary = "下载完成：成功 " + ok + " 首，歌曲失败 " + failedTracks + " 首";
            if (failedItems > 0) summary += "；有 " + failedItems + " 个下载项目未能读取";
            if (albums.Count > 0) summary += "\r\n专辑封面：已保存 " + covers + " 张";
            summary += "\r\n保存到：" + Path.GetFullPath(outputDirectory);
            foreach (var album in albums.Take(10)) summary += "\r\n" + album.DirectoryPath;
            if (errors.Count > 0) summary += "\r\n\r\n" + string.Join("\r\n", errors.Take(10));
            if (warnings.Count > 0) summary += "\r\n\r\n封面未保存（不影响歌曲下载）：\r\n" + string.Join("\r\n", warnings.Take(10));
            Emit("DONE", ok.ToString(CultureInfo.InvariantCulture), failedTracks.ToString(CultureInfo.InvariantCulture),
                PayloadCodec.EncodeText(summary));
        }

        private sealed class DownloadJob
        {
            internal OnlineTrack Track;
            internal string Directory;
            internal OnlineAlbumDownload Album;
            internal int AlbumIndex;
        }

        private static string UniqueFileName(string directory, string fileName)
        {
            return Path.GetFileName(OnlineAlbumDownload.UniquePath(directory, fileName, false));
        }

        private static string ArgText(string[] args, int index)
        {
            if (args.Length <= index) throw new InvalidOperationException("参数不完整");
            return PayloadCodec.DecodeText(args[index]);
        }

        private static int PositiveInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed : fallback;
        }

        private static string PlatformLabel(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case "tx": case "qq": return "QQ音乐";
                case "kg": case "kugou": return "酷狗音乐";
                case "wy": case "netease": return "网易云音乐";
                case "kw": case "kuwo": return "酷我音乐";
                case "mg": case "migu": return "咪咕音乐";
                case "qs": case "qishui": return "汽水音乐";
                default: return id ?? string.Empty;
            }
        }

        private static string UserMessage(Exception error)
        {
            while (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1) error = aggregate.InnerExceptions[0];
            // Repository errors already contain a readable summary. Appending
            // their inner exception repeats it and can expose source URL tokens.
            var text = System.Text.RegularExpressions.Regex.Replace(error.Message ?? string.Empty, "https?://\\S+", "远程请求").Trim();
            return string.IsNullOrWhiteSpace(text) ? "未知错误" : text;
        }

        private static void Ok(string message) => Emit("OK", PayloadCodec.EncodeText(message));

        private static void Emit(params string[] fields)
        {
            Console.WriteLine(string.Join("\t", fields ?? Array.Empty<string>()));
            Console.Out.Flush();
        }

        private sealed class RepositoryScope : IDisposable
        {
            internal RepositoryScope(OnlineMusicRepository value) { Value = value; }
            internal OnlineMusicRepository Value { get; }
            public void Dispose() { Value.ResetRuntimeOnly(); }
        }
    }

    internal sealed class BridgePayload
    {
        internal string Kind, PlatformId, LxSourceId, PlatformLabel, Id, Title, Artist, Album;
        internal long DurationMs;
        internal string ArtworkUrl, Qualities, MusicInfoJson, PreferredQuality, CollectionKind;

        internal OnlineTrack ToTrack()
        {
            var platform = OnlinePlatform.FromId(PlatformId.Length == 0 ? LxSourceId : PlatformId);
            var qualities = (Qualities ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(OnlineQuality.FromId).Where(item => item != null).Distinct().ToList();
            return new OnlineTrack
            {
                Platform = platform,
                Id = Id,
                Title = Title,
                Artist = Artist,
                Album = Album,
                DurationMs = DurationMs,
                ArtworkUrl = ArtworkUrl,
                AvailableQualities = qualities,
                MusicInfoJson = string.IsNullOrWhiteSpace(MusicInfoJson) ? MinimalMusicInfo(platform) : MusicInfoJson
            };
        }

        private string MinimalMusicInfo(OnlinePlatform platform)
        {
            return new JObject
            {
                ["source"] = platform.LxSourceId, ["id"] = Id, ["songId"] = Id, ["songmid"] = Id,
                ["name"] = Title, ["title"] = Title, ["singer"] = Artist, ["artist"] = Artist,
                ["albumName"] = Album, ["interval"] = DurationMs <= 0 ? 0 : DurationMs / 1000
            }.ToString(Formatting.None);
        }

        internal OnlineCollection ToCollection()
        {
            var kind = Kind == "album" ? OnlineCollectionKind.Album : OnlineCollectionKind.Playlist;
            return new OnlineCollection
            {
                Platform = OnlinePlatform.FromId(PlatformId.Length == 0 ? LxSourceId : PlatformId),
                Id = Id, Title = Title, Subtitle = Artist, ArtworkUrl = ArtworkUrl, Kind = kind
            };
        }
    }

    internal static class PayloadCodec
    {
        private const int FieldCount = 15;

        internal static string FromTrack(OnlineTrack track, string preferredQuality)
        {
            return EncodeFields(new[]
            {
                "1", "track", track.Platform.Id, track.Platform.LxSourceId, track.Platform.Label,
                track.Id ?? "", track.Title ?? "", track.Artist ?? "", track.Album ?? "",
                track.DurationMs.ToString(CultureInfo.InvariantCulture), track.ArtworkUrl ?? "",
                string.Join(",", track.AvailableQualities.Select(q => q.Id)), track.MusicInfoJson ?? "",
                preferredQuality ?? "320k", ""
            });
        }

        internal static string FromCollection(OnlineCollection collection, string kind, string preferredQuality)
        {
            return EncodeFields(new[]
            {
                "1", kind, collection.Platform.Id, collection.Platform.LxSourceId, collection.Platform.Label,
                collection.Id ?? "", collection.Title ?? "", collection.Subtitle ?? "", "",
                "0", collection.ArtworkUrl ?? "", "", "", preferredQuality ?? "320k",
                collection.Kind.ToString()
            });
        }

        internal static BridgePayload Parse(string encoded)
        {
            var fields = DecodeFields(encoded);
            if (fields.Count != FieldCount || fields[0] != "1") throw new InvalidOperationException("在线项目数据格式不受支持");
            long.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration);
            return new BridgePayload
            {
                Kind = fields[1], PlatformId = fields[2], LxSourceId = fields[3], PlatformLabel = fields[4],
                Id = fields[5], Title = fields[6], Artist = fields[7], Album = fields[8], DurationMs = duration,
                ArtworkUrl = fields[10], Qualities = fields[11], MusicInfoJson = fields[12],
                PreferredQuality = fields[13], CollectionKind = fields[14]
            };
        }

        internal static string EncodeText(string value) => Base64Url(Encoding.UTF8.GetBytes(value ?? string.Empty));
        internal static string DecodeText(string value) => Encoding.UTF8.GetString(FromBase64Url(value ?? string.Empty));

        private static string EncodeFields(IEnumerable<string> values)
        {
            using (var stream = new MemoryStream())
            {
                foreach (var value in values)
                {
                    var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                    var prefix = Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
                    stream.Write(prefix, 0, prefix.Length);
                    stream.Write(bytes, 0, bytes.Length);
                }
                return Base64Url(stream.ToArray());
            }
        }

        private static List<string> DecodeFields(string encoded)
        {
            var data = FromBase64Url(encoded);
            var fields = new List<string>();
            var offset = 0;
            while (offset < data.Length)
            {
                var colon = Array.IndexOf(data, (byte)':', offset);
                if (colon < 0) throw new InvalidOperationException("在线项目数据损坏");
                var countText = Encoding.ASCII.GetString(data, offset, colon - offset);
                if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0 || colon + 1 + count > data.Length)
                    throw new InvalidOperationException("在线项目数据损坏");
                offset = colon + 1;
                fields.Add(Encoding.UTF8.GetString(data, offset, count));
                offset += count;
            }
            return fields;
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static byte[] FromBase64Url(string text)
        {
            var base64 = text.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            return Convert.FromBase64String(base64);
        }
    }
}
