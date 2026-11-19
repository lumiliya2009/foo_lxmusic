using System;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    public sealed class ComponentUpdate
    {
        public string Version { get; set; }
        public string Url { get; set; }
    }

    public static class UpdateChecker
    {
        private const string Releases = "https://github.com/lumiliya2009/foo_lxmusic/releases/tag/";

        public static ComponentUpdate Check(string current)
        {
            var response = OnlineHttp.Execute("https://api.github.com/repos/lumiliya2009/foo_lxmusic/releases/latest",
                timeoutMs: 15000, maxBytes: 1024 * 1024);
            if (!response.Ok) throw new InvalidOperationException("检查更新失败（HTTP " + response.StatusCode + "），请稍后重试");
            return Parse(response.Body, current);
        }

        public static ComponentUpdate Parse(string json, string current)
        {
            var root = JObject.Parse(json);
            if (root.Value<bool?>("draft") == true || root.Value<bool?>("prerelease") == true) return null;
            var tag = root.Value<string>("tag_name") ?? string.Empty;
            var version = ParseVersion(tag);
            if (version == null) throw new InvalidOperationException("发布页版本号无法识别");
            var installed = ParseVersion(current) ?? throw new InvalidOperationException("当前版本号无法识别");
            if (version <= installed) return null;
            var url = root.Value<string>("html_url") ?? string.Empty;
            if (url != Releases + tag || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
                throw new InvalidOperationException("更新地址不属于本项目的 GitHub 发布页");
            return new ComponentUpdate { Version = tag, Url = url };
        }

        private static Version ParseVersion(string text)
        {
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            if (!Version.TryParse(text, out var version)) return null;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }
    }
}
