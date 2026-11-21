using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LxMusic.Bridge.Online
{
    public static class SourceMetadata
    {
        // Read only leading comments, never execute a script just to show its details.
        public static Dictionary<string, string> Read(string script)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = (script ?? string.Empty).TrimStart('\uFEFF');
            var offset = 0;
            while (offset < text.Length)
            {
                while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
                if (offset + 1 >= text.Length || text[offset] != '/') break;
                string comment;
                if (text[offset + 1] == '*')
                {
                    var end = text.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                    if (end < 0) break;
                    comment = text.Substring(offset + 2, end - offset - 2);
                    offset = end + 2;
                }
                else if (text[offset + 1] == '/')
                {
                    var end = text.IndexOf('\n', offset + 2);
                    if (end < 0) end = text.Length;
                    comment = text.Substring(offset + 2, end - offset - 2);
                    offset = end;
                }
                else break;

                string current = null;
                foreach (var raw in comment.Split('\n'))
                {
                    var line = raw.Trim().TrimStart('*').Trim();
                    var match = Regex.Match(line, @"^@([a-zA-Z]+)(?:[ \t]+(.*))?$");
                    if (match.Success)
                    {
                        current = match.Groups[1].Value.ToLowerInvariant();
                        fields[current] = match.Groups[2].Value.Trim();
                    }
                    else if (current == "description" && line.Length > 0)
                        fields[current] += "\n" + line;
                    else if (line.StartsWith("@"))
                        current = null;
                }
            }
            return fields;
        }

        public static LxSourceDescriptor Complete(LxSourceDescriptor descriptor, string script)
        {
            var fields = Read(script);
            Func<string, string, string> value = (name, existing) => !string.IsNullOrWhiteSpace(existing)
                ? existing
                : fields.TryGetValue(name, out var found) ? found : string.Empty;
            if (string.IsNullOrWhiteSpace(descriptor.Name) || descriptor.Name == "LX 在线音源")
                descriptor.Name = value("name", null);
            if (string.IsNullOrWhiteSpace(descriptor.Name)) descriptor.Name = "LX 在线音源";
            descriptor.Version = value("version", descriptor.Version);
            descriptor.Author = value("author", descriptor.Author);
            descriptor.Description = value("description", descriptor.Description);
            descriptor.Homepage = value("homepage", descriptor.Homepage);
            return descriptor;
        }

        public static string Details(OnlineSourceState source)
        {
            var descriptor = source.Descriptor;
            var text = new StringBuilder();
            text.AppendLine("名称：" + source.Name);
            text.AppendLine("版本：" + OrEmpty(descriptor.Version));
            text.AppendLine("作者：" + OrEmpty(descriptor.Author));
            text.AppendLine("支持站点：" + string.Join("、", descriptor.Sources.Keys.Select(key => PlatformLabel(descriptor, key))));
            text.AppendLine("支持音质：");
            foreach (var pair in descriptor.Sources)
            {
                var qualities = pair.Value.Select(QualityLabel).Distinct().ToArray();
                text.AppendLine(PlatformLabel(descriptor, pair.Key) + "（" +
                    (qualities.Length == 0 ? "未声明" : string.Join("、", qualities)) + "）");
            }
            text.AppendLine("描述：" + OrEmpty(descriptor.Description));
            if (!string.IsNullOrWhiteSpace(descriptor.Homepage)) text.AppendLine("主页：" + descriptor.Homepage);
            return text.ToString().TrimEnd();
        }

        private static string OrEmpty(string value) => string.IsNullOrWhiteSpace(value) ? "未提供" : value;

        private static string PlatformLabel(LxSourceDescriptor descriptor, string key)
        {
            try
            {
                var platform = OnlinePlatform.FromId(key);
                if (platform.Label != platform.Id) return platform.Label;
            }
            catch (InvalidOperationException) { }
            return descriptor.Names.TryGetValue(key, out var name) && !string.IsNullOrWhiteSpace(name) ? name : key;
        }

        private static string QualityLabel(string token)
        {
            var quality = OnlineQuality.FromId(token);
            if (quality == OnlineQuality.Standard) return "标准音质";
            if (quality == OnlineQuality.High) return "高品音质";
            if (quality == OnlineQuality.Lossless) return "无损音质";
            if (quality == OnlineQuality.HiRes) return "Hi-Res / 24bit";
            if (quality == OnlineQuality.Master) return "母带音质";
            return quality?.Label ?? token;
        }
    }
}
