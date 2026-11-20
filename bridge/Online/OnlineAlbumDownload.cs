using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LxMusic.Bridge.Online
{
    internal sealed class OnlineAlbumDownload
    {
        internal OnlineCollection Album { get; private set; }
        internal List<OnlineTrack> Tracks { get; private set; }
        internal string DirectoryPath { get; private set; }

        internal static OnlineAlbumDownload Prepare(OnlineCollection album, string directory,
            Func<OnlineCollection, int, OnlineSearchPage> readPage)
        {
            if (album == null || album.Kind != OnlineCollectionKind.Album)
                throw new InvalidOperationException("所选项目不是专辑");
            var tracks = new List<OnlineTrack>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var pageNumber = 1; ; pageNumber++)
            {
                if (pageNumber > 1000) throw new InvalidOperationException("专辑页数异常，已停止读取");
                var page = readPage(album, pageNumber);
                if (page == null) throw new InvalidOperationException("无法读取专辑曲目");
                var added = 0;
                foreach (var track in page.Tracks ?? new List<OnlineTrack>())
                {
                    if (track == null || string.IsNullOrWhiteSpace(track.Id)) continue;
                    if (!seen.Add((track.Platform?.Id ?? album.Platform.Id) + ":" + track.Id)) continue;
                    if (string.IsNullOrWhiteSpace(track.Album)) track.Album = album.Title;
                    tracks.Add(track);
                    added++;
                }
                if (!page.HasNext) break;
                if (added == 0) throw new InvalidOperationException("专辑接口重复返回同一页或缺少曲目，未下载不完整专辑");
            }
            if (tracks.Count == 0) throw new InvalidOperationException("专辑没有返回歌曲");

            var name = SafeName(string.Join(" - ", new[] { album.Subtitle, album.Title }
                .Where(value => !string.IsNullOrWhiteSpace(value))));
            var folder = UniquePath(Path.GetFullPath(directory), name, true);
            Directory.CreateDirectory(folder);
            return new OnlineAlbumDownload { Album = album, Tracks = tracks, DirectoryPath = folder };
        }

        internal string TrackFileName(int index, ResolvedOnlineMedia media, string namingTemplate = null)
        {
            return DownloadFileNaming.BuildFileName(namingTemplate ?? DownloadFileNaming.AlbumTemplate,
                Tracks[index], media, index + 1, Tracks.Count);
        }

        internal string SaveArtwork(Func<string, byte[]> fetch = null)
        {
            fetch = fetch ?? FetchArtwork;
            // QQ search results carry a small thumbnail. Use the same 500px
            // album artwork URL as our track parser, then fall back to catalog URLs.
            var preferred = ReferenceEquals(Album.Platform, OnlinePlatform.Qq) && !string.IsNullOrWhiteSpace(Album.Id)
                ? new[] { "https://y.gtimg.cn/music/photo_new/T002R500x500M000" + Uri.EscapeDataString(Album.Id) + ".jpg" }
                : Array.Empty<string>();
            var urls = preferred.Concat(new[] { Album.ArtworkUrl }).Concat(Tracks.Select(track => track.ArtworkUrl))
                .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal).Take(4);
            Exception lastError = null;
            foreach (var url in urls)
            {
                try
                {
                    return SaveArtworkBytes(DirectoryPath, fetch(url));
                }
                catch (Exception ex) { lastError = ex; }
            }
            throw new InvalidOperationException(lastError == null ? "专辑没有提供封面" : "无法获取有效的专辑封面", lastError);
        }

        private static byte[] FetchArtwork(string url)
        {
            var response = OnlineHttp.Execute(url, maxBytes: 8 * 1024 * 1024);
            if (!response.Ok) throw new IOException("封面服务器返回 HTTP " + response.StatusCode);
            return response.BodyBytes;
        }

        internal static string SaveArtworkBytes(string directory, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("封面为空或超过 8 MB");
            string extension;
            if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) extension = ".jpg";
            else if (StartsWith(bytes, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) extension = ".png";
            else if (StartsWith(bytes, "GIF87a") || StartsWith(bytes, "GIF89a")) extension = ".gif";
            else if (bytes.Length >= 12 && StartsWith(bytes, "RIFF") &&
                System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") extension = ".webp";
            else throw new InvalidOperationException("封面地址返回的内容不是支持的图片");
            Directory.CreateDirectory(directory);
            var path = UniquePath(directory, "cover" + extension, false);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                output.Write(bytes, 0, bytes.Length);
            return path;
        }

        private static bool StartsWith(byte[] bytes, string text) => StartsWith(bytes, System.Text.Encoding.ASCII.GetBytes(text));
        private static bool StartsWith(byte[] bytes, byte[] prefix) => bytes.Length >= prefix.Length && bytes.Take(prefix.Length).SequenceEqual(prefix);

        internal static string SafeName(string name)
        {
            var result = TextUtil.SanitizeFileName(name).TrimEnd(' ', '.');
            if (Regex.IsMatch(result, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) result = "_" + result;
            return result;
        }

        internal static string UniquePath(string directory, string name, bool folder)
        {
            var stem = folder ? name : Path.GetFileNameWithoutExtension(name);
            var extension = folder ? "" : Path.GetExtension(name);
            var path = Path.Combine(directory, name);
            for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
                path = Path.Combine(directory, stem + " (" + suffix + ")" + extension);
            return path;
        }
    }
}
