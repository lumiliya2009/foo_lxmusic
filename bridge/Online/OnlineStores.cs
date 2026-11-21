using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    /// <summary>本地数据保护：使用 Windows DPAPI 加密敏感文本，失败时退化为明文。</summary>
    internal static class ProtectedFile
    {
        public static void WriteAllText(string path, string content)
        {
            var plain = Encoding.UTF8.GetBytes(content ?? string.Empty);
            byte[] payload;
            try
            {
                payload = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                payload = Combine(new byte[] { 1 }, payload);
            }
            catch
            {
                payload = Combine(new byte[] { 0 }, plain);
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, payload);
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static string ReadAllText(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var payload = File.ReadAllBytes(path);
                if (payload.Length < 1) return null;
                var body = new byte[payload.Length - 1];
                Buffer.BlockCopy(payload, 1, body, 0, body.Length);
                if (payload[0] == 1)
                {
                    body = ProtectedData.Unprotect(body, null, DataProtectionScope.CurrentUser);
                }
                return Encoding.UTF8.GetString(body);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Combine(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }

    /// <summary>已导入的在线音源（含脚本原文）。</summary>
    public sealed class OnlineSourceStore
    {
        private readonly object _sync = new object();
        private readonly string _filePath = AppPaths.File("online-source.json");
        private List<StoredOnlineSource> _sources = new List<StoredOnlineSource>();
        private string _activeId;
        private bool _loaded;

        public OnlineSourceState State
        {
            get
            {
                lock (_sync)
                {
                    var stored = Load();
                    if (stored == null) return new OnlineSourceState();
                    return Describe(stored);
                }
            }
        }

        public StoredOnlineSource Current
        {
            get { lock (_sync) { return Load(); } }
        }

        public IReadOnlyList<OnlineSourceState> List()
        {
            lock (_sync) { Load(); return _sources.Select(Describe).ToArray(); }
        }

        public StoredOnlineSource Find(string id)
        {
            lock (_sync)
            {
                Load();
                var source = _sources.FirstOrDefault(item => item.Id == id);
                if (source == null) throw new InvalidOperationException("音源不存在，请重新打开音源列表");
                return JsonConvert.DeserializeObject<StoredOnlineSource>(JsonConvert.SerializeObject(source));
            }
        }

        public static string Revision(StoredOnlineSource source)
        {
            using (var hash = SHA256.Create())
            {
                var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(source));
                return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
        }

        public void ReplaceScript(string id, string revision, string script, LxSourceDescriptor descriptor)
        {
            Mutate(() =>
            {
                var source = _sources.FirstOrDefault(item => item.Id == id);
                if (source == null) throw new InvalidOperationException("音源已删除，无法保存；编辑内容已保留");
                if (Revision(source) != revision)
                    throw new InvalidOperationException("音源已被其他操作修改，请重新打开后编辑；本次内容已保留");
                source.Script = script;
                source.Descriptor = descriptor;
            });
        }

        private static OnlineSourceState Describe(StoredOnlineSource stored)
        {
            var descriptor = JsonConvert.DeserializeObject<LxSourceDescriptor>(
                JsonConvert.SerializeObject(stored.Descriptor ?? new LxSourceDescriptor()));
            SourceMetadata.Complete(descriptor, stored.Script);
            return new OnlineSourceState
            {
                Id = stored.Id,
                Name = !string.IsNullOrWhiteSpace(descriptor.Name) && descriptor.Name != "LX 在线音源"
                    ? descriptor.Name
                    : string.IsNullOrWhiteSpace(stored.DisplayName) ? "已导入在线音源" : stored.DisplayName,
                Version = descriptor.Version,
                Host = SafeHost(stored.Url),
                UrlAvailable = !string.IsNullOrWhiteSpace(stored.Url),
                Imported = true,
                Descriptor = descriptor
            };
        }

        public void Save(string url, string script, LxSourceDescriptor descriptor, string displayName, string origin = null)
        {
            Mutate(() =>
            {
                var old = _sources.FirstOrDefault(item => item.Script == script ||
                    (!string.IsNullOrEmpty(url) && item.Url == url) ||
                    (!string.IsNullOrEmpty(origin) && string.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase)));
                var stored = new StoredOnlineSource
                {
                    Id = old?.Id ?? Guid.NewGuid().ToString("N"),
                    Origin = origin ?? string.Empty,
                    Url = url ?? string.Empty,
                    Script = script ?? string.Empty,
                    Descriptor = descriptor ?? new LxSourceDescriptor(),
                    DisplayName = (displayName ?? string.Empty).Trim()
                };
                if (stored.DisplayName.Length > 80) stored.DisplayName = stored.DisplayName.Substring(0, 80);
                if (old == null) _sources.Add(stored);
                else _sources[_sources.IndexOf(old)] = stored;
                _activeId = stored.Id;
            });
        }

        public void Select(string id)
        {
            Mutate(() =>
            {
                if (!_sources.Any(item => item.Id == id)) throw new InvalidOperationException("音源不存在，请重新打开音源列表");
                _activeId = id;
            });
        }

        // Pin a playback/search operation to its selected source without
        // changing the active choice for other bridge processes.
        public void Use(string id)
        {
            lock (_sync)
            {
                Load();
                if (!_sources.Any(item => item.Id == id)) throw new InvalidOperationException("所选音源已删除，请重新选择音源");
                _activeId = id;
            }
        }

        public void Remove(string id = null)
        {
            Mutate(() =>
            {
                var target = id ?? _activeId;
                if (!_sources.Any(item => item.Id == target)) throw new InvalidOperationException("音源不存在，请刷新音源列表");
                _sources.RemoveAll(item => item.Id == target);
                if (!_sources.Any(item => item.Id == _activeId)) _activeId = _sources.FirstOrDefault()?.Id ?? string.Empty;
            });
        }

        private void Mutate(Action action)
        {
            lock (_sync)
            using (var mutex = new Mutex(false, "Local\\FooLxMusicSources-" + TextUtil.Md5Hex(_filePath.ToLowerInvariant())))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("音源库正忙，请稍后重试");
                try
                {
                    _loaded = false;
                    Load();
                    action();
                    // Keep the active source in the legacy fields so older
                    // component versions can still read the current source.
                    var active = _sources.FirstOrDefault(item => item.Id == _activeId);
                    var root = JObject.FromObject(active ?? new StoredOnlineSource());
                    root["libraryVersion"] = 1;
                    root["activeId"] = _activeId;
                    root["sources"] = JArray.FromObject(_sources);
                    ProtectedFile.WriteAllText(_filePath, root.ToString(Formatting.None));
                }
                finally { _loaded = false; mutex.ReleaseMutex(); }
            }
        }

        private StoredOnlineSource Load()
        {
            if (!_loaded)
            {
                _sources = new List<StoredOnlineSource>();
                _activeId = string.Empty;
                if (File.Exists(_filePath))
                {
                    var text = ProtectedFile.ReadAllText(_filePath);
                    if (string.IsNullOrWhiteSpace(text)) throw new IOException("无法读取音源库，原文件未修改");
                    try
                    {
                        var root = JObject.Parse(text);
                        if (root["libraryVersion"] != null)
                        {
                            if (root.Value<int>("libraryVersion") != 1 || !(root["sources"] is JArray))
                                throw new FormatException("unsupported library");
                            _sources = root["sources"].ToObject<List<StoredOnlineSource>>();
                            _activeId = root.Value<string>("activeId");
                            if (_sources.Any(item => item == null || string.IsNullOrWhiteSpace(item.Script) ||
                                !Guid.TryParseExact(item.Id, "N", out _))) throw new FormatException("invalid source");
                            if (_sources.Select(item => item.Id).Distinct().Count() != _sources.Count) throw new FormatException("duplicate IDs");
                        }
                        else
                        {
                            var legacy = root.ToObject<StoredOnlineSource>();
                            if (string.IsNullOrWhiteSpace(legacy.Script)) throw new FormatException("empty source");
                            // Stable until the first save, even across processes.
                            legacy.Id = TextUtil.Md5Hex(legacy.Script);
                            _sources.Add(legacy);
                            _activeId = legacy.Id;
                        }
                        if (!_sources.Any(item => item.Id == _activeId)) _activeId = _sources.FirstOrDefault()?.Id ?? string.Empty;
                    }
                    catch (Exception ex) { throw new IOException("音源库格式损坏，原文件未修改", ex); }
                }
                _loaded = true;
            }
            return _sources.FirstOrDefault(item => item.Id == _activeId);
        }

        private static string SafeHost(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Host;
            return "订阅地址";
        }
    }

    /// <summary>已授权的平台账号（Cookie）。</summary>
    public sealed class OnlineAccountStore
    {
        private readonly object _sync = new object();
        private readonly string _filePath = AppPaths.File("online-accounts.json");
        private Dictionary<string, StoredOnlineAccount> _accounts;
        private bool _loaded;

        public IReadOnlyList<OnlineAccountSummary> Summaries
        {
            get
            {
                lock (_sync)
                {
                    EnsureLoaded();
                    return _accounts.Values.Select(a => a.Summary).ToList();
                }
            }
        }

        public StoredOnlineAccount Account(OnlinePlatform platform)
        {
            lock (_sync)
            {
                EnsureLoaded();
                return _accounts.TryGetValue(platform.Id, out var account) ? account : null;
            }
        }

        public OnlineAccountSummary Save(OnlinePlatform platform, string cookie, string userId, string displayName)
        {
            var normalized = OnlineAccountCookies.Normalize(cookie);
            if (string.IsNullOrWhiteSpace(normalized)) throw new InvalidOperationException("没有获取到登录凭据");
            var summary = new OnlineAccountSummary
            {
                Platform = platform,
                UserId = (userId ?? string.Empty).Trim(),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? platform.Label : displayName.Trim(),
                AuthorizedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (summary.UserId.Length > 128) summary.UserId = summary.UserId.Substring(0, 128);
            if (summary.DisplayName.Length > 100) summary.DisplayName = summary.DisplayName.Substring(0, 100);

            lock (_sync)
            {
                EnsureLoaded();
                _accounts[platform.Id] = new StoredOnlineAccount { Summary = summary, Cookie = normalized };
                Persist();
            }
            return summary;
        }

        public void Remove(OnlinePlatform platform)
        {
            lock (_sync)
            {
                EnsureLoaded();
                if (_accounts.Remove(platform.Id)) Persist();
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _accounts = new Dictionary<string, StoredOnlineAccount>(StringComparer.OrdinalIgnoreCase);
            var text = ProtectedFile.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<StoredOnlineAccountDto>>(text) ?? new List<StoredOnlineAccountDto>();
                foreach (var item in list)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Cookie)) continue;
                    var platform = OnlinePlatform.FromId(item.Platform);
                    _accounts[platform.Id] = new StoredOnlineAccount
                    {
                        Cookie = OnlineAccountCookies.Normalize(item.Cookie),
                        Summary = new OnlineAccountSummary
                        {
                            Platform = platform,
                            UserId = item.UserId ?? string.Empty,
                            DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? platform.Label : item.DisplayName,
                            AuthorizedAt = item.AuthorizedAt
                        }
                    };
                }
            }
            catch
            {
                _accounts.Clear();
            }
        }

        private void Persist()
        {
            try
            {
                var list = _accounts.Values.Select(a => new StoredOnlineAccountDto
                {
                    Platform = a.Summary.Platform.Id,
                    UserId = a.Summary.UserId,
                    DisplayName = a.Summary.DisplayName,
                    AuthorizedAt = a.Summary.AuthorizedAt,
                    Cookie = a.Cookie
                }).ToList();
                ProtectedFile.WriteAllText(_filePath, JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch
            {
            }
        }

        private sealed class StoredOnlineAccountDto
        {
            public string Platform { get; set; }
            public string UserId { get; set; }
            public string DisplayName { get; set; }
            public long AuthorizedAt { get; set; }
            public string Cookie { get; set; }
        }
    }

    internal static class OnlineAccountCookies
    {
        public static string Normalize(IEnumerable<string> values)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawValue in values)
            {
                var headerValue = (rawValue ?? string.Empty).Trim();
                if (headerValue.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                    headerValue = headerValue.Substring(headerValue.IndexOf(':') + 1);
                foreach (var part in headerValue.Split(';'))
                {
                    var item = part.Trim();
                    var separator = item.IndexOf('=');
                    if (separator <= 0) continue;
                    var name = item.Substring(0, separator).Trim();
                    var value = item.Substring(separator + 1).Trim();
                    if (name.Length == 0 || name.Length > 128) continue;
                    if (value.Length > 8192 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) continue;
                    fields[name] = value;
                }
            }
            var result = string.Join("; ", fields.Select(pair => pair.Key + "=" + pair.Value));
            if (result.Length > 64 * 1024) throw new InvalidOperationException("登录凭据过大");
            return result;
        }

        public static string Normalize(string value) => Normalize(new[] { value });

        public static Dictionary<string, string> Parse(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in Normalize(value).Split(';'))
            {
                var item = part.Trim();
                var separator = item.IndexOf('=');
                if (separator <= 0) continue;
                result[item.Substring(0, separator)] = item.Substring(separator + 1);
            }
            return result;
        }

        public static string Value(IDictionary<string, string> cookies, params string[] names)
        {
            foreach (var wanted in names)
            {
                foreach (var pair in cookies)
                {
                    if (string.Equals(pair.Key, wanted, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                        return pair.Value;
                }
            }
            return string.Empty;
        }
    }
}
