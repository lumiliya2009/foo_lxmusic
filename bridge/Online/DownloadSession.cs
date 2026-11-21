using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LxMusic.Bridge.Online
{
    internal sealed class DownloadSession
    {
        private readonly OnlineMusicRepository repository;
        private readonly string output, template;
        private readonly OnlineQuality quality;
        private readonly bool skipFailures, cleanup;
        private readonly int workers;
        private readonly object sync = new object();
        private readonly CancellationTokenSource failureCancellation = new CancellationTokenSource();
        private readonly Action<string[]> emit;
        private readonly Func<Exception, string> message;
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> completed = new List<string>(), errors = new List<string>(), warnings = new List<string>();
        private readonly List<OnlineAlbumDownload> albums = new List<OnlineAlbumDownload>();
        private int ok, failedTracks, failedItems, failedPages, covers, cancelled, rankingItems;
        private volatile bool stopped;
        internal bool Stopped { get => stopped; private set => stopped = value; }
        internal int PagesRead { get; set; }

        internal DownloadSession(OnlineMusicRepository repository, string output, string quality, string template,
            bool skipFailures, bool cleanup, Action<string[]> emit, Func<Exception, string> message, int workers = 1)
        {
            this.repository = repository;
            this.output = Path.GetFullPath(output);
            this.quality = OnlineQuality.FromId(quality) ?? OnlineQuality.High;
            this.template = template;
            if (template != null) DownloadFileNaming.ValidateTemplate(template);
            this.skipFailures = skipFailures; this.cleanup = cleanup; this.emit = emit; this.message = message;
            if (workers < 1 || workers > 16) throw new InvalidOperationException("同时下载任务数必须为 1～16");
            this.workers = workers;
        }

        internal void Status(string text) => emit(new[] { "STATUS", PayloadCodec.EncodeText(text) });
        internal void Stop(string reason) { Stopped = true; errors.Add(reason); }
        internal void PageFailed(int page, Exception ex)
        {
            failedPages++;
            errors.Add("第 " + page + " 页：" + message(ex));
            if (!skipFailures) Stopped = true;
        }

        private sealed class Job
        {
            internal OnlineTrack Track;
            internal string Directory;
            internal OnlineAlbumDownload Album;
            internal int AlbumIndex;
            internal int Number;
        }

        internal void Process(List<string> payloads, int offset = 0, int totalHint = 0, string pageLabel = "")
        {
            var jobs = new List<Job>();
            var pageAlbums = new List<OnlineAlbumDownload>();
            foreach (var encoded in payloads)
            {
                BridgePayload payload = null;
                try
                {
                    payload = PayloadCodec.Parse(encoded);
                    if (!seen.Add(payload.Kind + ":" + payload.PlatformId + ":" + payload.Id)) continue;
                    if (payload.Kind == "track") jobs.Add(new Job { Track = payload.ToTrack(), Directory = output });
                    else if (payload.Kind == "album")
                    {
                        Status("正在读取整张专辑：" + payload.Title);
                        var album = OnlineAlbumDownload.Prepare(payload.ToCollection(), output, OnlineCollectionSearch.AlbumTracks);
                        albums.Add(album); pageAlbums.Add(album);
                        for (var i = 0; i < album.Tracks.Count; i++)
                            jobs.Add(new Job { Track = album.Tracks[i], Directory = album.DirectoryPath, Album = album, AlbumIndex = i });
                    }
                    else if (payload.Kind == "playlist" && payload.CollectionKind == "Ranking")
                    {
                        Status("正在读取整个榜单：" + payload.Title);
                        var ranking = payload.ToCollection();
                        var tracks = RankingTracks(ranking);
                        if (tracks.Count == 0) throw new InvalidOperationException("榜单没有返回歌曲");
                        rankingItems++;
                        foreach (var track in tracks)
                        {
                            if (!seen.Add("track:" + track.Platform.Id + ":" + track.Id)) continue;
                            jobs.Add(new Job { Track = track, Directory = output });
                        }
                    }
                    else throw new InvalidOperationException("仅支持下载歌曲、专辑和榜单");
                }
                catch (Exception ex)
                {
                    failedItems++;
                    errors.Add((payload?.Title ?? "下载项目") + "：" + message(ex));
                    if (!skipFailures) { Stopped = true; return; }
                }
            }
            foreach (var album in pageAlbums)
            {
                Status("正在保存专辑封面：" + album.Album.Title);
                try { album.SaveArtwork(); covers++; }
                catch (Exception ex) { warnings.Add("封面未保存：" + album.Album.Title + "：" + message(ex)); }
            }
            var looseTotal = Math.Max(totalHint, offset + jobs.Count(job => job.Album == null));
            var looseNumber = offset;
            foreach (var job in jobs) if (job.Album == null) job.Number = ++looseNumber;
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processed = 0;
            Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, (index, loop) =>
            {
                if (Stopped) { loop.Stop(); return; }
                var job = jobs[index]; var track = job.Track;
                var label = (pageLabel.Length == 0 ? "" : pageLabel + " · ") + track;
                try
                {
                    failureCancellation.Token.ThrowIfCancellationRequested();
                    var media = repository.Resolve(track, quality);
                    failureCancellation.Token.ThrowIfCancellationRequested();
                    var name = job.Album == null ? OnlineDownloader.BuildFileName(track, media, template, job.Number, looseTotal)
                        : job.Album.TrackFileName(job.AlbumIndex, media, template);
                    string fileName;
                    lock (sync)
                    {
                        var path = Path.Combine(job.Directory, name);
                        for (var suffix = 2; reserved.Contains(path) || File.Exists(path) || Directory.Exists(path); suffix++)
                            path = Path.Combine(job.Directory, Path.GetFileNameWithoutExtension(name) + " (" + suffix + ")" + Path.GetExtension(name));
                        reserved.Add(path);
                        fileName = Path.GetFileName(path);
                    }
                    var lastReport = DateTime.MinValue;
                    Status("正在下载 " + (index + 1) + "/" + jobs.Count + "：" + label);
                    var saved = OnlineDownloader.Download(media.Url, job.Directory, fileName, (received, total) =>
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds < 100 && (total <= 0 || received < total)) return;
                        lastReport = now;
                        emit(new[] { "TRANSFER", Number(Volatile.Read(ref processed) + 1), Number(jobs.Count), Number(received), Number(Math.Max(0, total)), PayloadCodec.EncodeText(label) });
                    }, failureCancellation.Token);
                    lock (sync) { completed.Add(saved); ok++; }
                }
                catch (Exception ex)
                {
                    lock (sync)
                    {
                        if (failureCancellation.IsCancellationRequested) cancelled++;
                        else { failedTracks++; errors.Add(label + "：" + message(ex)); }
                        if (!skipFailures) Stopped = true;
                    }
                    if (!skipFailures) { failureCancellation.Cancel(); loop.Stop(); }
                }
                emit(new[] { "PROGRESS", Number(Interlocked.Increment(ref processed)), Number(jobs.Count), PayloadCodec.EncodeText(label) });
            });
        }

        internal void Finish()
        {
            var removed = 0;
            if (cleanup && completed.Count > 0)
            {
                Status("下载结束，正在按文件内容查重…");
                try { removed = DownloadDuplicateCleaner.Clean(output, completed, Status, text => warnings.Add(text)); }
                catch (Exception ex) { warnings.Add("查重未完成，文件已保留：" + message(ex)); }
            }
            var summary = (Stopped ? "已停止下载" : "下载完成") + "：成功 " + ok + " 首，歌曲失败 " + failedTracks + " 首";
            if (cancelled > 0) summary += "；中止 " + cancelled + " 个进行中的任务";
            if (failedItems > 0) summary += "；有 " + failedItems + " 个下载项目未能读取";
            if (PagesRead > 0 || failedPages > 0) summary += "\r\n搜索页面：已读取 " + PagesRead + " 页，失败 " + failedPages + " 页";
            if (cleanup) summary += "\r\n查重：删除 " + removed + " 个重复文件，保留 " + (ok - removed) + " 个新文件";
            if (albums.Count > 0) summary += "\r\n专辑封面：已保存 " + covers + " 张";
            if (rankingItems > 0) summary += "\r\n榜单：已读取 " + rankingItems + " 个";
            summary += "\r\n保存到：" + output;
            foreach (var album in albums.Take(10)) summary += "\r\n" + album.DirectoryPath;
            if (errors.Count > 0) summary += "\r\n\r\n" + string.Join("\r\n", errors.Take(10));
            if (warnings.Count > 0) summary += "\r\n\r\n" + string.Join("\r\n", warnings.Take(10));
            emit(new[] { "DONE", Number(ok), Number(failedTracks), PayloadCodec.EncodeText(summary) });
        }

        private List<OnlineTrack> RankingTracks(OnlineCollection ranking)
        {
            var tracks = new List<OnlineTrack>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            const int maxPages = 100;
            for (var page = 1; page <= maxPages; page++)
            {
                var result = OnlineCatalogBrowser.RankingTracks(ranking, page);
                foreach (var track in result.Tracks)
                    if (track != null && !string.IsNullOrWhiteSpace(track.Id) && ids.Add(track.Id))
                        tracks.Add(track);
                if (!result.HasNext || result.Tracks.Count == 0) break;
            }
            return tracks;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
