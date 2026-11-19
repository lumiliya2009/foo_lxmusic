using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
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
        private StoredOnlineSource _stored;
        private bool _loaded;

        public OnlineSourceState State
        {
            get
            {
                lock (_sync)
                {
                    var stored = Load();
                    if (stored == null) return new OnlineSourceState();
                    return new OnlineSourceState
                    {
                        Name = string.IsNullOrWhiteSpace(stored.DisplayName)
                            ? (string.IsNullOrWhiteSpace(stored.Descriptor?.Name) ? "已导入在线音源" : stored.Descriptor.Name)
                            : stored.DisplayName,
                        Version = stored.Descriptor?.Version ?? string.Empty,
                        Host = SafeHost(stored.Url),
                        Imported = true,
                        Descriptor = stored.Descriptor ?? new LxSourceDescriptor()
                    };
                }
            }
        }

        public StoredOnlineSource Current
        {
            get { lock (_sync) { return Load(); } }
        }

        public void Save(string url, string script, LxSourceDescriptor descriptor, string displayName)
        {
            lock (_sync)
            {
                _stored = new StoredOnlineSource
                {
                    Url = url ?? string.Empty,
                    Script = script ?? string.Empty,
                    Descriptor = descriptor ?? new LxSourceDescriptor(),
                    DisplayName = (displayName ?? string.Empty).Trim()
                };
                if (_stored.DisplayName.Length > 80) _stored.DisplayName = _stored.DisplayName.Substring(0, 80);
                _loaded = true;
                ProtectedFile.WriteAllText(_filePath, JsonConvert.SerializeObject(_stored, Formatting.Indented));
            }
        }

        public void Remove()
        {
            lock (_sync)
            {
                _stored = null;
                _loaded = true;
                try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
            }
        }

        private StoredOnlineSource Load()
        {
            if (_loaded) return _stored;
            _loaded = true;
            var text = ProtectedFile.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { _stored = JsonConvert.DeserializeObject<StoredOnlineSource>(text); }
            catch { _stored = null; }
            if (_stored != null && string.IsNullOrWhiteSpace(_stored.Script)) _stored = null;
            return _stored;
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
