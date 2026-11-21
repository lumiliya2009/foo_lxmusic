using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace LxMusic.Bridge.Online
{
    internal sealed class OnlineHttpResponse
    {
        public int StatusCode { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body { get; set; } = string.Empty;
        public byte[] BodyBytes { get; set; } = Array.Empty<byte>();
        public string FinalUrl { get; set; } = string.Empty;
        public bool Ok => StatusCode >= 200 && StatusCode <= 299;
    }

    internal sealed class OnlineHttpRequestHandle
    {
        private readonly object _sync = new object();
        private HttpWebRequest _active;
        private bool _cancelled;

        public void Cancel()
        {
            HttpWebRequest request;
            lock (_sync)
            {
                _cancelled = true;
                request = _active;
                _active = null;
            }
            try { request?.Abort(); } catch { }
        }

        internal bool Cancelled
        {
            get { lock (_sync) { return _cancelled; } }
        }

        internal void Attach(HttpWebRequest request)
        {
            var cancel = false;
            lock (_sync)
            {
                if (_cancelled) cancel = true;
                else _active = request;
            }
            if (cancel) { try { request.Abort(); } catch { } }
        }

        internal void Detach(HttpWebRequest request)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, request)) _active = null;
            }
        }
    }

    internal static class OnlineHttp
    {
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

        private const int DefaultTimeoutMs = 20000;
        private const int DefaultMaxBytes = 2 * 1024 * 1024;
        private const int MaxRedirects = 5;

        public static OnlineHttpResponse Execute(
            string url,
            string method = "GET",
            IDictionary<string, string> headers = null,
            byte[] body = null,
            int timeoutMs = DefaultTimeoutMs,
            int maxBytes = DefaultMaxBytes,
            int followMax = MaxRedirects,
            OnlineHttpRequestHandle handle = null,
            bool useSystemProxy = false,
            bool forceDirect = false,
            Action<long, long> progress = null)
        {
            var currentUrl = ValidatePublicHttpUrl(url);
            var currentMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();
            var currentBody = body;
            var redirectLimit = Math.Max(0, Math.Min(10, followMax));

            for (var redirectCount = 0; redirectCount <= redirectLimit; redirectCount++)
            {
                if (handle != null && handle.Cancelled) throw new IOException("请求已取消");

                var request = (HttpWebRequest)WebRequest.Create(currentUrl);
                // Windows/IE automatic proxy discovery can add several seconds to every
                // request on machines with a stale local proxy. Catalog calls
                // use the direct fast path, but all imported-script requests
                // must honor the system proxy, including provider domains.
                if (forceDirect || (!useSystemProxy && IsKnownMusicHost(new Uri(currentUrl).Host))) request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.Method = currentMethod;
                request.Timeout = Math.Max(2000, Math.Min(60000, timeoutMs));
                request.ReadWriteTimeout = Math.Max(2000, Math.Min(60000, timeoutMs));
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                request.UserAgent = BrowserUserAgent;
                request.Accept = "application/json, text/plain, */*";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        if (string.IsNullOrWhiteSpace(header.Key)) continue;
                        if (!IsSafeHeader(header.Key)) continue;
                        if (IsRestrictedHeader(header.Key))
                        {
                            ApplyRestrictedHeader(request, header.Key, header.Value);
                            continue;
                        }
                        try { request.Headers[header.Key] = header.Value; } catch { }
                    }
                }

                if (currentBody != null && currentBody.Length > 0 && currentMethod != "GET" && currentMethod != "HEAD")
                {
                    request.ContentLength = currentBody.Length;
                    request.ContentType = request.ContentType ?? "application/x-www-form-urlencoded; charset=utf-8";
                }

                handle?.Attach(request);
                try
                {
                    if (handle != null && handle.Cancelled) throw new IOException("请求已取消");

                    if (currentBody != null && currentBody.Length > 0 && currentMethod != "GET" && currentMethod != "HEAD")
                    {
                        using (var stream = request.GetRequestStream())
                        {
                            stream.Write(currentBody, 0, currentBody.Length);
                        }
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        var status = (int)response.StatusCode;
                        if (status >= 300 && status < 400)
                        {
                            var location = response.Headers["Location"];
                            if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("在线服务返回了无效跳转");
                            if (redirectCount >= redirectLimit) throw new InvalidOperationException("在线服务跳转次数过多");
                            currentUrl = ValidatePublicHttpUrl(new Uri(new Uri(currentUrl), location).ToString());
                            if (status == 303 || ((status == 301 || status == 302) && currentMethod == "POST"))
                            {
                                currentMethod = "GET";
                                currentBody = null;
                            }
                            continue;
                        }

                        var bytes = ReadLimited(response.GetResponseStream(), maxBytes, progress, response.ContentLength);
                        return Build(response, status, bytes, currentUrl);
                    }
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse errorResponse)
                {
                    using (errorResponse)
                    {
                        var status = (int)errorResponse.StatusCode;
                        if (status >= 300 && status < 400)
                        {
                            var location = errorResponse.Headers["Location"];
                            if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("在线服务返回了无效跳转");
                            if (redirectCount >= redirectLimit) throw new InvalidOperationException("在线服务跳转次数过多");
                            currentUrl = ValidatePublicHttpUrl(new Uri(new Uri(currentUrl), location).ToString());
                            if (status == 303 || ((status == 301 || status == 302) && currentMethod == "POST"))
                            {
                                currentMethod = "GET";
                                currentBody = null;
                            }
                            continue;
                        }

                        byte[] errorBytes;
                        try { errorBytes = ReadLimited(errorResponse.GetResponseStream(), maxBytes); }
                        catch { errorBytes = Array.Empty<byte>(); }
                        return Build(errorResponse, status, errorBytes, currentUrl);
                    }
                }
                finally
                {
                    handle?.Detach(request);
                }
            }

            throw new InvalidOperationException("在线服务跳转次数过多");
        }

        private static OnlineHttpResponse Build(HttpWebResponse response, int status, byte[] bytes, string currentUrl)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in response.Headers.AllKeys)
            {
                if (key == null) continue;
                headers[key] = response.Headers[key];
            }

            var charset = Encoding.UTF8;
            var contentType = response.ContentType;
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                var match = System.Text.RegularExpressions.Regex.Match(contentType, "charset\\s*=\\s*[\"']?([^;\"']+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    try { charset = Encoding.GetEncoding(match.Groups[1].Value.Trim()); } catch { charset = Encoding.UTF8; }
                }
            }

            return new OnlineHttpResponse
            {
                StatusCode = status,
                StatusMessage = response.StatusDescription ?? string.Empty,
                Headers = headers,
                Body = charset.GetString(bytes),
                BodyBytes = bytes,
                FinalUrl = string.IsNullOrWhiteSpace(response.ResponseUri?.ToString()) ? currentUrl : response.ResponseUri.ToString()
            };
        }

        public static string Query(string baseUrl, IDictionary<string, object> parameters)
        {
            if (parameters == null || parameters.Count == 0) return baseUrl;
            var separator = baseUrl.IndexOf('?') >= 0 ? "&" : "?";
            var parts = new List<string>();
            foreach (var pair in parameters)
            {
                if (pair.Value == null) continue;
                parts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
            return parts.Count == 0 ? baseUrl : baseUrl + separator + string.Join("&", parts);
        }

        public static string ValidatePublicHttpUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("地址为空");
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) throw new InvalidOperationException("地址格式无效");
            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只支持 HTTP 或 HTTPS 地址");
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("地址不能包含用户名或密码");
            if (string.IsNullOrWhiteSpace(uri.Host)) throw new InvalidOperationException("地址缺少服务器");

            IPAddress[] addresses;
            try { addresses = Dns.GetHostAddresses(uri.Host); }
            catch (Exception ex) { throw new IOException("无法解析在线服务器：" + uri.Host, ex); }
            if (addresses.Length == 0) throw new IOException("无法解析在线服务器：" + uri.Host);
            foreach (var address in addresses)
            {
                if (IsPrivateAddress(address)) throw new InvalidOperationException("出于安全原因，在线音源不能访问本机或局域网地址");
            }
            return uri.ToString();
        }

        private static bool IsPrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
                if (bytes[0] == 127) return true;
                if (bytes[0] >= 224) return true;
            }
            return false;
        }

        private static bool IsKnownMusicHost(string host)
        {
            var value = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            string[] domains =
            {
                "music.163.com", "126.net", "qq.com", "gtimg.cn", "kugou.com",
                "kuwo.cn", "migu.cn", "lrclib.net", "voxly.cn"
            };
            return domains.Any(domain => value == domain || value.EndsWith("." + domain, StringComparison.Ordinal));
        }

        private static bool IsSafeHeader(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "host":
                case "content-length":
                case "connection":
                case "transfer-encoding":
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsRestrictedHeader(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "user-agent":
                case "accept":
                case "referer":
                case "content-type":
                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyRestrictedHeader(HttpWebRequest request, string name, string value)
        {
            if (value == null) return;
            switch (name.ToLowerInvariant())
            {
                case "user-agent": request.UserAgent = value; break;
                case "accept": request.Accept = value; break;
                case "referer": request.Referer = value; break;
                case "content-type": request.ContentType = value; break;
            }
        }

        private static byte[] ReadLimited(Stream input, int maxBytes, Action<long, long> progress = null, long total = -1)
        {
            if (input == null) return Array.Empty<byte>();
            var limit = Math.Max(1024, Math.Min(32 * 1024 * 1024, maxBytes));
            using (var memory = new MemoryStream(Math.Min(limit, 64 * 1024)))
            {
                var buffer = new byte[16 * 1024];
                int read;
                var lastReport = DateTime.MinValue;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + read > limit) throw new InvalidOperationException("在线响应过大");
                    memory.Write(buffer, 0, read);
                    if (progress != null)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds >= 120)
                        {
                            lastReport = now;
                            progress(memory.Length, total);
                        }
                    }
                }
                if (progress != null) progress(memory.Length, total);
                return memory.ToArray();
            }
        }

        public static string DetectMediaMimeType(string contentType, byte[] bytes, string url)
        {
            if (bytes != null && bytes.Length >= 4)
            {
                var magic = DetectMagic(bytes);
                if (!string.IsNullOrEmpty(magic)) return magic;
            }

            var header = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
            switch (header)
            {
                case "audio/x-flac":
                case "application/flac": return "audio/flac";
                case "audio/x-m4a":
                case "video/mp4":
                case "application/mp4": return "audio/mp4";
                case "audio/x-wav":
                case "audio/wave": return "audio/wav";
                case "audio/x-aac": return "audio/aac";
                case "application/vnd.apple.mpegurl":
                case "application/x-mpegurl":
                case "audio/mpegurl": return "application/x-mpegURL";
                case "application/octet-stream":
                case "binary/octet-stream": return string.Empty;
                default:
                    return header.StartsWith("audio/") ? header : string.Empty;
            }
        }

        private static string DetectMagic(byte[] bytes)
        {
            if (StartsWithAscii(bytes, "fLaC")) return "audio/flac";
            if (StartsWithAscii(bytes, "ID3")) return "audio/mpeg";
            if (StartsWithAscii(bytes, "OggS")) return "audio/ogg";
            if (StartsWithAscii(bytes, "MAC ")) return "audio/ape";
            if (StartsWithAscii(bytes, "RIFF") && StartsWithAscii(bytes, "WAVE", 8)) return "audio/wav";
            if (StartsWithAscii(bytes, "ftyp", 4)) return "audio/mp4";
            if (StartsWithAscii(bytes, "#EXTM3U")) return "application/x-mpegURL";
            if (bytes.Length >= 2 && bytes[0] == 0xff && (bytes[1] & 0xf0) == 0xf0)
                return (bytes[1] & 0x06) == 0 ? "audio/aac" : "audio/mpeg";
            return null;
        }

        private static bool StartsWithAscii(byte[] bytes, string value, int offset = 0)
        {
            if (bytes == null || offset < 0 || bytes.Length < offset + value.Length) return false;
            for (var i = 0; i < value.Length; i++)
            {
                if (bytes[offset + i] != value[i]) return false;
            }
            return true;
        }
    }
}
