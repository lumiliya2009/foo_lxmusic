using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LxMusic.Bridge.Online
{
    /// <summary>
    /// LX 自定义音源宿主（Jint + 官方 preload）。
    /// 与 Android 版 LxSourceRuntime 使用同一套 native 协议。
    /// </summary>
    public sealed class LxSourceRuntime : IDisposable
    {
        private const string PreloadResource = "LxMusic.Resources.user-api-preload.js";
        private const int DefaultRequestTimeoutMs = 20000;
        private const int DefaultFollowMax = 5;
        private const int MaxScriptResponseBytes = 4 * 1024 * 1024;
        private const string BinaryMarker = "__tzBinary";

        private readonly string _key;
        private readonly string _script;
        private readonly Thread _thread;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly TaskCompletionSource<LxSourceDescriptor> _initialized =
            new TaskCompletionSource<LxSourceDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        private readonly ConcurrentDictionary<string, OnlineHttpRequestHandle> _activeRequests =
            new ConcurrentDictionary<string, OnlineHttpRequestHandle>();

        private Engine _engine;
        private volatile bool _closed;
        private LxSourceDescriptor _descriptor;
        private Exception _loadError;

        private LxSourceRuntime(string script, string key)
        {
            _script = script;
            _key = key;
            _thread = new Thread(ThreadLoop)
            {
                IsBackground = true,
                Name = "LxSourceRuntime-" + key
            };
        }

        public LxSourceDescriptor Descriptor => _descriptor;

        public static LxSourceRuntime Create(string script, string key, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("在线音源脚本为空");
            var runtime = new LxSourceRuntime(script, key);
            runtime._thread.SetApartmentState(ApartmentState.MTA);
            runtime._thread.Start();
            if (!runtime._initialized.Task.Wait(timeout))
            {
                runtime.Dispose();
                throw new TimeoutException("在线音源初始化超时");
            }
            runtime._initialized.Task.GetAwaiter().GetResult();
            return runtime;
        }

        private void ThreadLoop()
        {
            try
            {
                BuildEngine();
                RunPreload();
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    try { work(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _loadError = ex;
                if (!_initialized.Task.IsCompleted)
                    _initialized.TrySetException(new InvalidOperationException("在线音源脚本加载失败：" + ex.Message, ex));
            }
        }

        private void Post(Action action)
        {
            if (_closed) return;
            try { _queue.Add(action); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        private void BuildEngine()
        {
            var engine = new Engine(options => options
                .LimitRecursion(256)
                .MaxStatements(4_000_000)
                .Strict(false));

            engine.SetValue("__lx_native_call__", new Func<string, string, string, JsValue>(NativeCall));
            engine.SetValue("__lx_native_call__set_timeout", new Action<int, int>(NativeSetTimeout));
            engine.SetValue("__lx_native_call__utils_str2b64",
                new Func<string, string>(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty))));
            engine.SetValue("__lx_native_call__utils_b642buf", new Func<string, string>(Base64ToJsonArray));
            engine.SetValue("__lx_native_call__utils_str2md5", new Func<string, string>(TextUtil.Md5Hex));
            engine.SetValue("__lx_native_call__utils_aes_encrypt", new Func<string, string, string, int, string>(AesEncrypt));
            engine.SetValue("__lx_native_call__utils_rsa_encrypt", new Func<string, string, int, string>(RsaEncrypt));
            engine.SetValue("__lx_native_call__utils_random_bytes", new Func<int, string>(size =>
            {
                var buffer = new byte[Math.Max(0, size)];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buffer);
                return Convert.ToBase64String(buffer);
            }));
            engine.SetValue("__hostLog", new Action<string>(message => Log?.Invoke(message)));

            engine.Execute(
                "var console = { log: function(){ __hostLog(Array.prototype.slice.call(arguments).join(' ')); }," +
                " info: function(){ __hostLog(Array.prototype.slice.call(arguments).join(' ')); }," +
                " warn: function(){ __hostLog(Array.prototype.slice.call(arguments).join(' ')); }," +
                " error: function(){ __hostLog(Array.prototype.slice.call(arguments).join(' ')); }," +
                " debug: function(){ __hostLog(Array.prototype.slice.call(arguments).join(' ')); } };");

            _engine = engine;
        }

        public event Action<string> Log;

        private void RunPreload()
        {
            var preload = ReadResource(PreloadResource);
            _engine.Execute(preload);
            _engine.Invoke("lx_setup", _key, _key, Metadata("name", "LX 在线音源"), Metadata("description", string.Empty),
                Metadata("version", string.Empty), Metadata("author", string.Empty), Metadata("homepage", string.Empty), _script);
            _engine.Execute(_script);
            DrainMicrotasks();
        }

        private string Metadata(string name, string fallback)
        {
            var match = System.Text.RegularExpressions.Regex.Match(_script,
                "(?im)^\\s*\\*?\\s*@" + System.Text.RegularExpressions.Regex.Escape(name) + "\\s+(.+?)\\s*$");
            var value = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string ReadResource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new FileNotFoundException("缺少内嵌资源：" + name);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        private JsValue NativeCall(string key, string action, string dataJson)
        {
            if (!string.Equals(key, _key, StringComparison.Ordinal)) return JsValue.FromObject(_engine, "Invalid key");
            try
            {
                switch (action)
                {
                    case "init": HandleInit(dataJson); break;
                    case "request": HandleScriptRequest(dataJson); break;
                    case "cancelRequest": HandleCancelRequest(dataJson); break;
                    case "response": HandleInvocationResponse(dataJson); break;
                    case "showUpdateAlert": break;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke("native call " + action + " failed: " + ex.Message);
            }
            return JsValue.Undefined;
        }

        private void HandleInit(string dataJson)
        {
            if (_initialized.Task.IsCompleted) return;
            try
            {
                var payload = JObject.Parse(dataJson);
                if (!(payload.Value<bool?>("status") ?? false))
                {
                    var message = payload.Value<string>("errorMessage");
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "在线音源初始化失败" : message);
                }
                var info = payload["info"] as JObject ?? payload;
                var descriptor = ParseDescriptor(info);
                _descriptor = descriptor;
                _initialized.TrySetResult(descriptor);
            }
            catch (Exception ex)
            {
                _initialized.TrySetException(ex);
            }
        }

        public static LxSourceDescriptor ParseDescriptor(JObject info)
        {
            var descriptor = new LxSourceDescriptor
            {
                Name = info.Value<string>("name") ?? "LX 在线音源",
                Version = info.Value<string>("version") ?? string.Empty
            };
            var sources = info["sources"] as JObject;
            if (sources != null)
            {
                foreach (var property in sources.Properties())
                {
                    var qualities = new List<string>();
                    var actions = new List<string> { "musicUrl" };
                    if (property.Value is JObject sourceObject)
                    {
                        var qualityToken = sourceObject["qualitys"] ?? sourceObject["qualities"];
                        if (qualityToken is JArray qualityArray)
                            qualities.AddRange(qualityArray.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)));
                        var actionToken = sourceObject["actions"] ?? sourceObject["action"];
                        if (actionToken is JArray actionArray)
                        {
                            actions = actionArray.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                            if (actions.Count == 0) actions.Add("musicUrl");
                        }
                    }
                    else if (property.Value is JArray simpleArray)
                    {
                        qualities.AddRange(simpleArray.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)));
                    }
                    descriptor.Sources[property.Name] = qualities;
                    descriptor.Actions[property.Name] = actions;
                }
            }
            if (descriptor.Sources.Count == 0) throw new InvalidOperationException("在线音源没有声明可用平台");
            return descriptor;
        }

        private void HandleScriptRequest(string dataJson)
        {
            var payload = JObject.Parse(dataJson);
            var requestKey = payload.Value<string>("requestKey");
            var url = payload.Value<string>("url");
            if (string.IsNullOrWhiteSpace(requestKey) || string.IsNullOrWhiteSpace(url)) return;
            var options = payload["options"] as JObject ?? new JObject();

            var handle = new OnlineHttpRequestHandle();
            if (_activeRequests.TryGetValue(requestKey, out var previous)) previous.Cancel();
            _activeRequests[requestKey] = handle;

            Task.Run(() =>
            {
                JObject response = null;
                string error = null;
                try
                {
                    response = ExecuteScriptRequest(url, options, handle);
                }
                catch (Exception ex)
                {
                    error = string.IsNullOrWhiteSpace(ex.Message) ? "在线请求失败" : ex.Message;
                }
                finally
                {
                    OnlineHttpRequestHandle removed;
                    _activeRequests.TryRemove(requestKey, out removed);
                }

                var completion = new JObject
                {
                    ["requestKey"] = requestKey,
                    ["error"] = error == null ? JValue.CreateNull() : new JValue(error),
                    ["response"] = response ?? (JToken)JValue.CreateNull()
                };
                Post(() =>
                {
                    CallScript("response", completion.ToString(Formatting.None));
                    DrainMicrotasks();
                });
            });
        }

        private void HandleCancelRequest(string dataJson)
        {
            var requestId = UnwrapScalar(dataJson);
            if (string.IsNullOrWhiteSpace(requestId)) return;
            if (_activeRequests.TryRemove(requestId, out var handle)) handle.Cancel();
        }

        private void HandleInvocationResponse(string dataJson)
        {
            JObject payload;
            try { payload = JObject.Parse(dataJson); }
            catch { return; }
            var requestKey = payload.Value<string>("requestKey");
            if (string.IsNullOrWhiteSpace(requestKey)) return;
            if (!_pending.TryRemove(requestKey, out var pending)) return;

            if (!(payload.Value<bool?>("status") ?? false))
            {
                var message = payload.Value<string>("errorMessage");
                pending.TrySetException(new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message) ? "在线音源请求失败" : message.Trim()));
                return;
            }

            var result = payload["result"];
            if (result is JObject resultObject && resultObject["data"] != null) result = resultObject["data"];
            pending.TrySetResult(result == null || result.Type == JTokenType.Null
                ? "null"
                : result.ToString(Formatting.None));
        }

        private static string UnwrapScalar(string dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return string.Empty;
            try
            {
                var token = JToken.Parse(dataJson);
                return token.Type == JTokenType.Null ? string.Empty : token.ToString();
            }
            catch
            {
                return dataJson.Trim().Trim('"');
            }
        }

        private void NativeSetTimeout(int id, int milliseconds)
        {
            var delay = Math.Max(0, Math.Min(60000, milliseconds));
            Task.Delay(delay).ContinueWith(_ =>
            {
                Post(() =>
                {
                    CallScript("__set_timeout__", id.ToString(CultureInfo.InvariantCulture));
                    DrainMicrotasks();
                });
            }, TaskScheduler.Default);
        }

        private void CallScript(string action, string dataJson)
        {
            if (_closed || _engine == null) return;
            _engine.Invoke("__lx_native__", _key, action, dataJson);
        }

        private void DrainMicrotasks()
        {
            try { _engine.Advanced.ProcessTasks(); } catch { }
        }

        /// <summary>调用音源处理一次 request 事件，返回 result.data 的 JSON 文本。</summary>
        public async Task<string> InvokeAsync(string action, string source, string infoJson, TimeSpan timeout)
        {
            if (_closed) throw new ObjectDisposedException(nameof(LxSourceRuntime));
            if (_loadError != null) throw new InvalidOperationException("在线音源脚本加载失败：" + _loadError.Message, _loadError);
            await _initialized.Task.ConfigureAwait(false);

            var requestId = "request__" + Guid.NewGuid().ToString("N");
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = pending;

            var payload = new JObject
            {
                ["requestKey"] = requestId,
                ["data"] = new JObject
                {
                    ["action"] = action,
                    ["source"] = source,
                    ["info"] = JToken.Parse(string.IsNullOrWhiteSpace(infoJson) ? "null" : infoJson)
                }
            };

            Post(() =>
            {
                try
                {
                    CallScript("request", payload.ToString(Formatting.None));
                    DrainMicrotasks();
                }
                catch (Exception ex)
                {
                    if (_pending.TryRemove(requestId, out var failed)) failed.TrySetException(ex);
                }
            });

            var completed = await Task.WhenAny(pending.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, pending.Task))
            {
                _pending.TryRemove(requestId, out _);
                throw new TimeoutException("在线音源请求超时");
            }
            return await pending.Task.ConfigureAwait(false);
        }

        private JObject ExecuteScriptRequest(string url, JObject options, OnlineHttpRequestHandle handle)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (options["headers"] is JObject headerObject)
            {
                foreach (var property in headerObject.Properties())
                {
                    var raw = property.Value;
                    string value;
                    if (raw is JArray array) value = string.Join(", ", array.Values<string>());
                    else if (raw.Type == JTokenType.Null) value = string.Empty;
                    else value = raw.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) headers[property.Name] = value;
                }
            }

            var body = BuildRequestBody(options, headers);
            var method = (options.Value<string>("method") ?? (body == null ? "GET" : "POST")).ToUpperInvariant();
            var response = OnlineHttp.Execute(
                url,
                method,
                headers,
                body,
                OptInt(options, "timeout", DefaultRequestTimeoutMs),
                OptInt(options, "maxBytes", MaxScriptResponseBytes),
                OptInt(options, "follow_max", OptInt(options, "followMax", DefaultFollowMax)),
                handle);

            var headerJson = new JObject();
            foreach (var header in response.Headers)
            {
                headerJson[header.Key] = header.Value;
                var lower = header.Key.ToLowerInvariant();
                if (headerJson[lower] == null) headerJson[lower] = header.Value;
            }

            var result = new JObject
            {
                ["statusCode"] = response.StatusCode,
                ["statusMessage"] = response.StatusMessage,
                ["url"] = response.FinalUrl,
                ["ok"] = response.Ok,
                ["headers"] = headerJson
            };

            if (options.Value<bool?>("binary") ?? false)
            {
                result["body"] = new JObject
                {
                    [BinaryMarker] = true,
                    ["base64"] = Convert.ToBase64String(response.BodyBytes)
                };
            }
            else
            {
                result["body"] = JsonOrText(response.Body);
            }
            return result;
        }

        private static JToken JsonOrText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new JValue(text ?? string.Empty);
            var trimmed = text.Trim();
            if (trimmed[0] == '{' || trimmed[0] == '[')
            {
                try { return JToken.Parse(trimmed); } catch { }
            }
            return new JValue(text);
        }

        private static byte[] BuildRequestBody(JObject options, Dictionary<string, string> headers)
        {
            byte[] body = null;
            var raw = options["body"] ?? options["data"];
            if (raw != null && raw.Type != JTokenType.Null) body = RawBodyBytes(raw, headers);
            if (options["form"] is JObject form)
            {
                body = Encoding.UTF8.GetBytes(EncodeForm(form));
                if (!headers.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    headers["Content-Type"] = "application/x-www-form-urlencoded; charset=utf-8";
            }
            if (options["formData"] is JObject formData)
            {
                body = EncodeMultipart(formData, headers);
            }
            return body;
        }

        private static byte[] RawBodyBytes(JToken raw, Dictionary<string, string> headers)
        {
            if (raw is JObject rawObject)
            {
                var buffer = BufferBytes(rawObject);
                if (buffer != null) return buffer;
                if (!headers.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    headers["Content-Type"] = "application/json; charset=utf-8";
                return Encoding.UTF8.GetBytes(rawObject.ToString(Formatting.None));
            }
            if (raw is JArray rawArray)
            {
                if (!headers.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    headers["Content-Type"] = "application/json; charset=utf-8";
                return Encoding.UTF8.GetBytes(rawArray.ToString(Formatting.None));
            }
            return Encoding.UTF8.GetBytes(raw.ToString());
        }

        private static byte[] BufferBytes(JObject value)
        {
            if (value.Value<bool?>(BinaryMarker) ?? false)
            {
                try { return Convert.FromBase64String(value.Value<string>("base64") ?? string.Empty); }
                catch { return null; }
            }
            if (!(value["data"] is JArray data)) return null;
            if (!string.Equals(value.Value<string>("type"), "Buffer", StringComparison.OrdinalIgnoreCase)) return null;
            var buffer = new byte[data.Count];
            for (var i = 0; i < data.Count; i++) buffer[i] = (byte)(data[i].Value<int>() & 0xff);
            return buffer;
        }

        private static string EncodeForm(JObject form)
        {
            var parts = new List<string>();
            foreach (var property in form.Properties())
            {
                var raw = property.Value;
                string text;
                if (raw.Type == JTokenType.Null) text = string.Empty;
                else if (raw is JArray array) text = string.Join(",", array.Values<string>());
                else text = raw.ToString();
                parts.Add(Uri.EscapeDataString(property.Name).Replace("+", "%20") + "=" + Uri.EscapeDataString(text).Replace("+", "%20"));
            }
            return string.Join("&", parts);
        }

        private static byte[] EncodeMultipart(JObject formData, Dictionary<string, string> headers)
        {
            var existing = headers.FirstOrDefault(k => k.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            var boundary = "----LxMusic" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(existing) || existing.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase) < 0)
                headers["Content-Type"] = "multipart/form-data; boundary=" + boundary;

            using (var memory = new MemoryStream())
            {
                foreach (var property in formData.Properties())
                {
                    var descriptor = property.Value as JObject;
                    var value = descriptor?["value"] ?? descriptor?["data"] ?? property.Value;
                    byte[] bytes;
                    if (value is JObject valueObject) bytes = BufferBytes(valueObject) ?? Encoding.UTF8.GetBytes(valueObject.ToString(Formatting.None));
                    else if (value is JArray valueArray) bytes = Encoding.UTF8.GetBytes(valueArray.ToString(Formatting.None));
                    else bytes = Encoding.UTF8.GetBytes(value?.ToString() ?? string.Empty);

                    var filename = descriptor?.Value<string>("filename") ?? string.Empty;
                    var contentType = descriptor?.Value<string>("contentType") ?? string.Empty;
                    var disposition = "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" +
                                      property.Name.Replace("\"", string.Empty) + "\"";
                    if (!string.IsNullOrWhiteSpace(filename))
                        disposition += "; filename=\"" + filename.Replace("\"", string.Empty) + "\"";
                    disposition += "\r\n";
                    if (!string.IsNullOrWhiteSpace(contentType)) disposition += "Content-Type: " + contentType + "\r\n";
                    disposition += "\r\n";
                    var headerBytes = Encoding.UTF8.GetBytes(disposition);
                    memory.Write(headerBytes, 0, headerBytes.Length);
                    memory.Write(bytes, 0, bytes.Length);
                    memory.Write(new byte[] { 13, 10 }, 0, 2);
                }
                var closing = Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");
                memory.Write(closing, 0, closing.Length);
                return memory.ToArray();
            }
        }

        private static int OptInt(JObject options, string name, int fallback)
        {
            var token = options[name];
            if (token == null) return fallback;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<int>();
            if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var parsed)) return parsed;
            return fallback;
        }

        private static string Base64ToJsonArray(string value)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(value ?? string.Empty); }
            catch { bytes = Array.Empty<byte>(); }
            var builder = new StringBuilder(bytes.Length * 4 + 2);
            builder.Append('[');
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(bytes[i].ToString(CultureInfo.InvariantCulture));
            }
            builder.Append(']');
            return builder.ToString();
        }

        private static string AesEncrypt(string dataBase64, string keyBase64, string ivBase64, int mode)
        {
            var data = Convert.FromBase64String(dataBase64 ?? string.Empty);
            var key = Convert.FromBase64String(keyBase64 ?? string.Empty);
            var iv = string.IsNullOrWhiteSpace(ivBase64) ? null : Convert.FromBase64String(ivBase64);
            if (key.Length != 16) throw new InvalidOperationException("AES 密钥长度不受支持");

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = iv == null ? CipherMode.ECB : CipherMode.CBC;
                aes.Padding = iv == null ? PaddingMode.None : PaddingMode.PKCS7;
                aes.IV = iv ?? new byte[16];
                using (var encryptor = aes.CreateEncryptor())
                {
                    var result = encryptor.TransformFinalBlock(data, 0, data.Length);
                    return Convert.ToBase64String(result);
                }
            }
        }

        private static string RsaEncrypt(string dataBase64, string keyBase64, int padding)
        {
            var data = Convert.FromBase64String(dataBase64 ?? string.Empty);
            var parameters = ParseSubjectPublicKeyInfo(keyBase64);
            var modulus = FromBigEndian(parameters.Modulus);
            var exponent = FromBigEndian(parameters.Exponent);
            var block = FromBigEndian(data);
            var encrypted = BigInteger.ModPow(block, exponent, modulus);
            var size = parameters.Modulus.Length;
            var bytes = ToBigEndian(encrypted);
            if (bytes.Length == size) return Convert.ToBase64String(bytes);
            var padded = new byte[size];
            Buffer.BlockCopy(bytes, 0, padded, size - bytes.Length, bytes.Length);
            return Convert.ToBase64String(padded);
        }

        /// <summary>把无符号大端字节序转换为 BigInteger（.NET Framework 没有 isUnsigned 重载）。</summary>
        private static BigInteger FromBigEndian(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return BigInteger.Zero;
            var little = new byte[bytes.Length + 1];
            for (var i = 0; i < bytes.Length; i++) little[i] = bytes[bytes.Length - 1 - i];
            return new BigInteger(little);
        }

        /// <summary>把非负 BigInteger 转换为无符号大端字节序。</summary>
        private static byte[] ToBigEndian(BigInteger value)
        {
            if (value.Sign < 0) throw new InvalidOperationException("不支持负数的大端转换");
            var little = value.ToByteArray();
            var length = little.Length;
            while (length > 1 && little[length - 1] == 0) length--;
            var result = new byte[length];
            for (var i = 0; i < length; i++) result[i] = little[length - 1 - i];
            return result;
        }

        private static RSAParameters ParseSubjectPublicKeyInfo(string keyBase64)
        {
            var key = Convert.FromBase64String(keyBase64 ?? string.Empty);
            var offset = 0;
            ReadSequence(key, ref offset);                  // SubjectPublicKeyInfo
            ReadSequence(key, ref offset);                  // AlgorithmIdentifier
            SkipElement(key, ref offset);
            if (key[offset] != 0x03) throw new InvalidOperationException("RSA 公钥格式无效");
            offset++;
            var bitStringLength = ReadLength(key, ref offset);
            offset++;                                        // unused bits
            var end = offset + bitStringLength - 1;
            ReadSequence(key, ref offset);                   // RSAPublicKey
            var modulus = ReadInteger(key, ref offset, out var modulusLength);
            var exponent = ReadInteger(key, ref offset, out var exponentLength);
            if (offset > end + 4 || modulusLength == 0 || exponentLength == 0)
                throw new InvalidOperationException("RSA 公钥格式无效");
            return new RSAParameters { Modulus = modulus, Exponent = exponent };
        }

        private static void ReadSequence(byte[] data, ref int offset)
        {
            if (data[offset] != 0x30) throw new InvalidOperationException("RSA 公钥格式无效");
            offset++;
            ReadLength(data, ref offset);
        }

        private static void SkipElement(byte[] data, ref int offset)
        {
            var length = ReadLength(data, ref offset);
            offset += length;
        }

        private static int ReadLength(byte[] data, ref int offset)
        {
            var first = data[offset++];
            if ((first & 0x80) == 0) return first;
            var count = first & 0x7f;
            var length = 0;
            for (var i = 0; i < count; i++) length = (length << 8) | data[offset++];
            return length;
        }

        private static byte[] ReadInteger(byte[] data, ref int offset, out int length)
        {
            if (data[offset] != 0x02) throw new InvalidOperationException("RSA 公钥格式无效");
            offset++;
            length = ReadLength(data, ref offset);
            var value = new byte[length];
            Buffer.BlockCopy(data, offset, value, 0, length);
            offset += length;
            if (value.Length > 1 && value[0] == 0)
            {
                var trimmed = new byte[value.Length - 1];
                Buffer.BlockCopy(value, 1, trimmed, 0, trimmed.Length);
                value = trimmed;
            }
            return value;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            foreach (var handle in _activeRequests.Values) handle.Cancel();
            _activeRequests.Clear();
            foreach (var pending in _pending.Values)
                pending.TrySetException(new OperationCanceledException("在线音源运行时已关闭"));
            _pending.Clear();
            try { _queue.CompleteAdding(); } catch { }
            try
            {
                if (!_thread.Join(TimeSpan.FromSeconds(3)))
                    _thread.Interrupt();
            }
            catch { }
            try { _queue.Dispose(); } catch { }
        }
    }
}
