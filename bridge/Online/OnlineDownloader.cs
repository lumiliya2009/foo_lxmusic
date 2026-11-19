using System;
using System.IO;
using System.Net;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    /// <summary>在线音乐下载（支持进度与取消）。</summary>
    public static class OnlineDownloader
    {
        public static string BuildFileName(OnlineTrack track, ResolvedOnlineMedia resolved)
        {
            var qualityLabel = string.IsNullOrWhiteSpace(resolved?.SourceQuality) ? resolved?.Quality?.Label : resolved.SourceQuality;
            var stem = TextUtil.SanitizeFileName(string.Join(" - ", new[]
            {
                track.Artist ?? string.Empty,
                track.Title ?? string.Empty,
                qualityLabel ?? string.Empty
            }.WherePart()));
            var extension = FileExtension(resolved?.Url, resolved?.MimeType);
            return stem + "-" + DateTime.Now.ToString("HHmmss") + "." + extension;
        }

        private static System.Collections.Generic.IEnumerable<string> WherePart(this string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }

        public static string FileExtension(string url, string mimeType)
        {
            string fromUrl = null;
            if (!string.IsNullOrWhiteSpace(url))
            {
                var path = url.Split('?')[0].Split('#')[0];
                var slash = path.LastIndexOf('/');
                var name = slash >= 0 ? path.Substring(slash + 1) : path;
                var dot = name.LastIndexOf('.');
                if (dot >= 0)
                {
                    var candidate = name.Substring(dot + 1).ToLowerInvariant();
                    if (candidate == "mp3" || candidate == "flac" || candidate == "m4a" || candidate == "aac" ||
                        candidate == "ogg" || candidate == "opus" || candidate == "wav") fromUrl = candidate;
                }
            }
            if (fromUrl != null) return fromUrl;
            switch ((mimeType ?? string.Empty).ToLowerInvariant())
            {
                case "audio/flac": return "flac";
                case "audio/mp4":
                    return "m4a";
                case "audio/aac": return "aac";
                case "audio/ogg": return "ogg";
                default: return "mp3";
            }
        }

        public static string Download(string url, string directory, string fileName,
            Action<long, long> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("下载地址为空", nameof(url));
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, fileName);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = OnlineHttp.BrowserUserAgent;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (cancellationToken.Register(request.Abort))
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024))
                {
                    var total = response.ContentLength;
                    var buffer = new byte[128 * 1024];
                    long received = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        received += read;
                        progress?.Invoke(received, total);
                    }
                    if (received == 0) throw new IOException("音源返回了空文件");
                    if (total >= 0 && received != total) throw new IOException("音频文件下载不完整");
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Never overwrite an existing file, even if another download
                // created it after the caller selected a unique name.
                File.Move(temporary, target);
                return target;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
