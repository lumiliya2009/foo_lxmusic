using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TzPlayer.Win.Online
{
    internal static class TextUtil
    {
        private static readonly Regex AudioExtension =
            new Regex(@"\.(?:mp3|flac|m4a|aac|ogg|opus|wav|ape)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string StripAudioFileExtension(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return AudioExtension.Replace(value.Trim(), string.Empty).Trim();
        }

        public static string HtmlDecode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&#39;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Trim();
        }

        public static string NormalizeSourceId(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) builder.Append(ch);
            }
            return builder.ToString();
        }

        public static string NormalizeQualityId(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value.Trim().ToLowerInvariant())
            {
                if (ch == ' ' || ch == '_' || ch == '-') continue;
                builder.Append(ch);
            }
            return builder.ToString();
        }

        public static string Md5Hex(string value)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        public static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "在线音乐";
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (ch < 32 || "\\/:*?\"<>|".IndexOf(ch) >= 0) builder.Append('_');
                else builder.Append(ch);
            }
            var result = builder.ToString().Trim(' ', '.');
            if (result.Length > 120) result = result.Substring(0, 120);
            return string.IsNullOrWhiteSpace(result) ? "在线音乐" : result;
        }

        public static int ToInt(object value, int fallback = 0)
        {
            if (value == null) return fallback;
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (value is double d) return (int)d;
            if (value is bool b) return b ? 1 : 0;
            if (value is string s)
            {
                if (int.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                if (double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl)) return (int)dbl;
            }
            return fallback;
        }

        public static long ToLong(object value, long fallback = 0)
        {
            if (value == null) return fallback;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is double d) return (long)d;
            if (value is bool b) return b ? 1 : 0;
            if (value is string s)
            {
                if (long.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                if (double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl)) return (long)dbl;
            }
            return fallback;
        }

        public static bool ToBool(object value, bool fallback = false)
        {
            if (value == null) return fallback;
            if (value is bool b) return b;
            if (value is int i) return i != 0;
            if (value is long l) return l != 0L;
            if (value is string s)
            {
                var trimmed = s.Trim();
                if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                if (long.TryParse(trimmed, out var parsed)) return parsed != 0L;
            }
            return fallback;
        }
    }
}
