using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    public sealed class ComponentUpdate
    {
        public string Version { get; set; }
        public string Url { get; set; }
        public string Architecture { get; set; }
        public string PackageName { get; set; }
        public string PackageUrl { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Changelog { get; set; } = string.Empty;
    }

    public static class UpdateChecker
    {
        private const string Releases = "https://github.com/lumiliya2009/foo_lxmusic/releases/tag/";
        private const string Downloads = "https://github.com/lumiliya2009/foo_lxmusic/releases/download/";
        public const int MaxPackageBytes = 32 * 1024 * 1024;
        private static readonly string[] Feeds = {
            "https://api.github.com/repos/lumiliya2009/foo_lxmusic/releases/latest",
            "https://raw.githubusercontent.com/lumiliya2009/foo_lxmusic/main/update.json",
            "https://cdn.jsdelivr.net/gh/lumiliya2009/foo_lxmusic@main/update.json",
            // Optional provider endpoints, used only as additional fallbacks.
            // Their availability is not guaranteed by jsDelivr.
            "https://fastly.jsdelivr.net/gh/lumiliya2009/foo_lxmusic@main/update.json",
            "https://gcore.jsdelivr.net/gh/lumiliya2009/foo_lxmusic@main/update.json"
        };

        public static ComponentUpdate Check(string current, string architecture = null,
            Func<string, bool, string> fetch = null)
        {
            if (ParseVersion(current) == null) throw new InvalidOperationException("当前版本号无法识别");
            if (architecture != null) PackageName("v0.0.0", architecture);
            var handles = Enumerable.Range(0, Feeds.Length * 2).Select(_ => new OnlineHttpRequestHandle()).ToArray();
            // Try the user's proxy and direct route independently. A dead local
            // proxy or a blocked API must not prevent the other feeds working.
            var tasks = handles.Select((handle, index) => Task.Factory.StartNew(() =>
            {
                try
                {
                    var url = Feeds[index / 2]; var direct = index % 2 == 1;
                    string json;
                    if (fetch != null) json = fetch(url, direct);
                    else
                    {
                        var response = OnlineHttp.Execute(url, timeoutMs: 8000, maxBytes: 1024 * 1024,
                            handle: handle, useSystemProxy: true, forceDirect: direct);
                        if (!response.Ok) return null;
                        json = response.Body;
                    }
                    return new FeedResult { Update = Parse(json, current, architecture) };
                }
                catch { return null; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try
            {
                Task.WaitAll(tasks, 18000);
                var valid = tasks.Where(task => task.Status == TaskStatus.RanToCompletion)
                    .Select(task => task.Result).Where(result => result != null).ToArray();
                if (valid.Length == 0) throw new IOException("检查更新失败：所有更新通道均不可用，请检查网络后重试");
                // CDN caches can lag behind GitHub. Compare every valid result.
                return valid.Where(result => result.Update != null).Select(result => result.Update)
                    .OrderByDescending(update => ParseVersion(update.Version)).FirstOrDefault();
            }
            finally { foreach (var handle in handles) handle.Cancel(); }
        }

        private sealed class FeedResult { internal ComponentUpdate Update; }

        public static ComponentUpdate Parse(string json, string current, string architecture = null)
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
            var update = new ComponentUpdate
            {
                Version = tag,
                Url = url,
                Changelog = NormalizeChangelog(root.Value<string>("body") ?? root.Value<string>("changelog"))
            };
            if (architecture == null) return update;
            update.Architecture = architecture;
            update.PackageName = PackageName(tag, architecture);
            var assets = (root["assets"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(asset => asset.Value<string>("name") == update.PackageName).ToArray();
            if (assets.Length != 1 || assets[0].Value<string>("state") != "uploaded")
                throw new InvalidOperationException("发布页缺少当前播放器对应的安装包，请稍后重试");
            var selected = assets[0];
            update.PackageUrl = selected.Value<string>("browser_download_url");
            update.Sha256 = selected.Value<string>("digest");
            update.Size = selected.Value<long?>("size") ?? 0;
            ValidatePackage(update);
            return update;
        }

        public static string PackageName(string tag, string architecture)
        {
            if (tag == null || !Regex.IsMatch(tag, @"\Av?\d+\.\d+\.\d+(?:\.\d+)?\z") || ParseVersion(tag) == null)
                throw new InvalidOperationException("更新版本号无效");
            var suffix = architecture == "x86" ? "fb1-x86" : architecture == "x64" ? "fb2-x64" : null;
            if (suffix == null) throw new InvalidOperationException("不支持的播放器架构");
            return "foo_lxmusic-" + tag.TrimStart('v') + "-" + suffix + ".fb2k-component";
        }

        public static string[] PackageUrls(ComponentUpdate update)
        {
            if (update == null) throw new InvalidOperationException("更新安装包地址无效");
            var repositoryPath = "lumiliya2009/foo_lxmusic@" + update.Version + "/downloads/" + update.PackageName;
            var releaseUrl = Downloads + update.Version + "/" + update.PackageName;
            return new[]
            {
                "https://gh-proxy.org/" + releaseUrl,
                "https://ghfast.top/" + releaseUrl,
                "https://gh-proxy.com/" + releaseUrl,
                "https://gcore.jsdelivr.net/gh/" + repositoryPath,
                "https://fastly.jsdelivr.net/gh/" + repositoryPath,
                "https://cdn.jsdelivr.net/gh/" + repositoryPath,
                releaseUrl
            };
        }

        public static void ValidatePackage(ComponentUpdate update)
        {
            if (update == null || update.PackageName != PackageName(update.Version, update.Architecture) ||
                update.Url != Releases + update.Version ||
                update.PackageUrl != Downloads + update.Version + "/" + update.PackageName)
                throw new InvalidOperationException("更新安装包地址无效");
            if (update.Size < 1 || update.Size > MaxPackageBytes || update.Sha256 == null ||
                !Regex.IsMatch(update.Sha256, @"\Asha256:[0-9a-fA-F]{64}\z"))
                throw new InvalidOperationException("更新安装包缺少有效的大小或 SHA256 校验信息");
        }

        private static string NormalizeChangelog(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length > 4096) text = text.Substring(0, 4096).TrimEnd();
            return text;
        }

        private static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            if (!Version.TryParse(text, out var version)) return null;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }
    }
}
