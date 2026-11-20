using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LxMusic.Bridge.Online
{
    public static class DownloadFileNaming
    {
        public const string DefaultTemplate = "{序号} - {曲名} - {艺术家}";
        public const string AlbumTemplate = "{序号} - {曲名}";
        public const int MaxTemplateLength = 256;

        public static void ValidateTemplate(string template)
        {
            Expand(template, token => Value(token, null, null, 1, 1));
        }

        public static string BuildFileName(string template, OnlineTrack track, ResolvedOnlineMedia media, int number, int total)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            if (number < 1 || total < number) throw new ArgumentOutOfRangeException(nameof(number), "下载序号无效");
            var stem = TextUtil.SanitizeFileName(Expand(template, token => Value(token, track, media, number, total)));
            // SanitizeFileName limits the stem to 120 UTF-16 units. Do not split an emoji.
            if (stem.Length > 0 && char.IsHighSurrogate(stem[stem.Length - 1])) stem = stem.Substring(0, stem.Length - 1);
            stem = stem.TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(stem)) stem = "在线音乐";
            if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase)) stem = "_" + stem;
            return stem + "." + OnlineDownloader.FileExtension(media?.Url, media?.MimeType);
        }

        public static string Preview(string template, OnlineQuality quality)
        {
            return BuildFileName(template,
                new OnlineTrack { Title = "示例歌曲", Artist = "示例艺术家", Album = "示例专辑" },
                new ResolvedOnlineMedia { Quality = quality, SourceQuality = quality?.Id,
                    MimeType = quality == OnlineQuality.Lossless || quality == OnlineQuality.HiRes ? "audio/flac" : "audio/mpeg" }, 1, 12);
        }

        private static string Value(string token, OnlineTrack track, ResolvedOnlineMedia media, int number, int total)
        {
            switch (token)
            {
                case "序号": return number.ToString("D" + Math.Max(2, total.ToString(CultureInfo.InvariantCulture).Length), CultureInfo.InvariantCulture);
                case "曲名":
                case "文件名": return string.IsNullOrWhiteSpace(track?.Title) ? "未命名歌曲" : track.Title;
                case "艺术家": return string.IsNullOrWhiteSpace(track?.Artist) ? "未知艺术家" : track.Artist;
                case "专辑名": return string.IsNullOrWhiteSpace(track?.Album) ? "未知专辑" : track.Album;
                case "音质": return string.IsNullOrWhiteSpace(media?.SourceQuality) ? media?.Quality?.Id ?? "未知音质" : media.SourceQuality;
                default: throw new InvalidOperationException("不支持的命名字段：{" + token + "}。可用：{序号}、{曲名}、{艺术家}、{专辑名}、{音质}");
            }
        }

        private static string Expand(string template, Func<string, string> value)
        {
            if (string.IsNullOrWhiteSpace(template)) throw new InvalidOperationException("请输入文件命名格式");
            if (template.Length > MaxTemplateLength) throw new InvalidOperationException("文件命名格式不能超过 256 个字符");
            var result = new StringBuilder();
            for (var i = 0; i < template.Length; i++)
            {
                if (template[i] == '}') throw new InvalidOperationException("命名字段的大括号不完整");
                if (template[i] != '{') { result.Append(template[i]); continue; }
                var end = template.IndexOf('}', i + 1);
                if (end < 0) throw new InvalidOperationException("命名字段的大括号不完整");
                result.Append(value(template.Substring(i + 1, end - i - 1)));
                i = end;
            }
            return result.ToString();
        }
    }
}
