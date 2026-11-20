using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using LxMusic.Bridge.Online;

static class QualityRegression
{
    static int checks;
    static string profile;
    static void Check(bool value, string reason) { System.Threading.Interlocked.Increment(ref checks); if (!value) throw new Exception(reason); }
    static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Decode(string text) { text = text.Replace('-', '+').Replace('_', '/'); return Encoding.UTF8.GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='))); }

    sealed class Result
    {
        public int Code;
        public List<string[]> Lines;
        public string[] Tried => Lines.Where(line => line[0] == "TRIED").Select(line => line[1]).ToArray();
        public string[] Url => Lines.Single(line => line[0] == "URL");
        public string Remaining => Lines.Single(line => line[0] == "REMAINING")[1];
    }

    static Result Bridge(string command, params string[] args)
    {
        // Every argument is a fixed command, Base64URL payload or quality ID.
        var start = new ProcessStartInfo(typeof(OnlineMusicRepository).Assembly.Location,
            string.Join(" ", new[] { command, Encode(profile) }.Concat(args)))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("bridge fixture timed out"); }
            Check(error.Result.Length == 0, "unexpected bridge stderr: " + error.Result);
            return new Result { Code = process.ExitCode, Lines = output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).ToList() };
        }
    }

    static string Payload(string mode = "ok", string qualities = "flac,320k,128k")
    {
        var fields = new[] { "1", "track", "qq", "tx", "fixture", "same-song", "测试歌曲", "测试歌手", "测试专辑", "240000", "", qualities,
            "{\"songId\":\"same-song\",\"mode\":\"" + mode + "\"}", "flac", "" };
        return Encode(string.Concat(fields.Select(value => Encoding.UTF8.GetByteCount(value) + ":" + value)));
    }

    static void TestOrdering()
    {
        var supported = new List<string> { "128k", "320k", "lossless", "flac", "flac24bit", "hires", "master", "unknown" };
        Check(OnlineMusicRepository.QualityTokensFor(OnlineQuality.Lossless, supported)
            .SequenceEqual(new[] { "lossless", "master", "flac24bit", "320k", "128k" }), "requested quality/alias priority incorrect");
        Check(OnlineMusicRepository.QualityTokensFor(OnlineQuality.High, supported,
            new[] { OnlineQuality.Lossless, OnlineQuality.Standard }).SequenceEqual(new[] { "lossless", "128k" }), "track/source intersection incorrect");
        Check(OnlineMusicRepository.QualityTokensFor(OnlineQuality.Standard, supported)
            .First() == "128k", "explicit low-quality preference was ignored");
        Check(OnlineMusicRepository.QualityTokensFor(OnlineQuality.Lossless, new List<string> { "unknown" }).Count == 0,
            "unrecognized source quality was accepted");
    }

    static void TestConsole()
    {
        const string script = @"
lx.on(lx.EVENT_NAMES.request, async ({info}) => {
    try {
        if (lx.env !== 'mobile') {
            console.groupEnd();
            const methods = ['assert','clear','debug','error','info','log','table','trace','warn',
                'dir','dirxml','count','countReset','group','groupCollapsed','groupEnd','time','timeLog','timeEnd'];
            for (const method of methods)
                if (typeof console[method] !== 'function') throw new Error('missing console method: ' + method);
            console.group('outer');
            console.groupCollapsed('inner');
            const circular = {}; circular.self = circular;
            const unprintable = {toString() {throw new Error('diagnostic formatting failed');}};
            for (const method of ['debug','error','info','log','table','trace','warn','dir','dirxml'])
                console[method](Symbol('fixture'), circular, unprintable);
            console.assert(true, 'must not be logged');
            console.assert(false, 'diagnostic assertion');
            console.count('requests'); console.count('requests'); console.countReset('requests'); console.count('requests');
            console.time('parse'); console.timeLog('parse', 'progress'); console.timeEnd('parse');
            console.timeEnd('missing'); console.countReset('missing');
            console.groupEnd(); console.groupEnd(); console.clear();
        }
        if (info.musicInfo.mode === 'source-error') throw new Error('fixture source failure');
        await new Promise(resolve => setTimeout(resolve, 1));
        return 'https://fixture.invalid/console-compatible';
    } finally {
        if (lx.env !== 'mobile') console.groupEnd();
    }
});
lx.send(lx.EVENT_NAMES.inited,{sources:{tx:{type:'music',actions:['musicUrl'],qualitys:['flac','320k','128k']}}});";
        using (var runtime = LxSourceRuntime.Create(script, "console", TimeSpan.FromSeconds(10)))
        {
            var logs = new List<string>();
            runtime.Log += logs.Add;
            var result = runtime.InvokeAsync("musicUrl", "tx", "{\"musicInfo\":{\"mode\":\"ok\"}}", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Check(result.Contains("fixture.invalid/console-compatible"), "desktop console interrupted source resolution");
            Check(logs.Count(line => line.Trim() == "requests: 1") == 2 && logs.Any(line => line.Trim() == "requests: 2"), "console counters did not reset");
            Check(logs.Count(line => line.TrimStart().StartsWith("parse: ")) == 2, "console timers did not log");
            Check(!logs.Any(line => line.Contains("must not be logged")) && logs.Any(line => line.Contains("diagnostic assertion")), "console assertion threw or ignored its condition");
            try
            {
                runtime.InvokeAsync("musicUrl", "tx", "{\"musicInfo\":{\"mode\":\"source-error\"}}", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Check(false, "source rejection was swallowed");
            }
            catch (Exception error) { Check(error.Message.Contains("fixture source failure"), "console cleanup hid the original source error"); }
            runtime.Log += message => { throw new InvalidOperationException("diagnostic sink failed"); };
            result = runtime.InvokeAsync("musicUrl", "tx", "{\"musicInfo\":{\"mode\":\"ok\"}}", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Check(result.Contains("fixture.invalid/console-compatible"), "diagnostic sink interrupted source resolution");
        }
        var originalProfile = profile;
        profile = Path.Combine(profile, "console-protocol");
        Directory.CreateDirectory(profile);
        try
        {
            var file = Path.Combine(profile, "console.js");
            File.WriteAllText(file, script);
            Check(Bridge("import-file", Encode(file)).Code == 0, "console fixture import failed");
            var result = Bridge("resolve", Payload(), "flac");
            Check(result.Code == 0 && result.Tried.SequenceEqual(new[] { "flac" }), "console calls caused unnecessary quality fallback");
            Check(result.Lines.All(line => new[] { "SOURCEID", "TRIED", "URL", "REMAINING" }.Contains(line[0])), "console output leaked into bridge protocol");
        }
        finally { profile = originalProfile; }
    }

    static void TestBridge()
    {
        var script = Path.Combine(profile, "fixture.js");
        File.WriteAllText(script, @"
lx.on(lx.EVENT_NAMES.request, function (request) {
    const type = request.info.type;
    const song = request.info.musicInfo;
    const mode = song.mode;
    if (mode === 'all' || (mode === 'reject_lossless' && type === 'lossless'))
        return Promise.reject(new Error('音质不可用'));
    if (mode !== 'ok' && mode !== 'reject_lossless') return Promise.reject(new Error(mode));
    return Promise.resolve('https://fixture.invalid/' + song.songId + '/' + type);
});
lx.send(lx.EVENT_NAMES.inited, { sources: { tx: { type: 'music', actions: ['musicUrl'],
    qualitys: ['128k', '320k', 'lossless', 'flac', 'flac24bit', 'hires'] } } });
", new UTF8Encoding(false));
        Check(Bridge("import-file", Encode(script)).Code == 0, "fixture source import failed");

        var first = Bridge("resolve", Payload(), "flac");
        Check(first.Code == 0 && first.Tried.SequenceEqual(new[] { "flac" }), "first attempt did not report canonical quality");
        Check(first.Url[2] == "flac" && Decode(first.Url[1]) == "https://fixture.invalid/same-song/lossless", "source alias or track ID changed");
        Check(first.Remaining == "2", "remaining qualities counted aliases twice");

        var next = Bridge("resolve", Payload(), "flac", "lossless");
        Check(next.Code == 0 && next.Tried.SequenceEqual(new[] { "320k" }), "excluded alias was tried again");
        Check(next.Remaining == "1" && Decode(next.Url[1]).Contains("/same-song/"), "fallback changed track or remaining count");

        var last = Bridge("resolve", Payload(), "flac", "flac,320kbps");
        Check(last.Tried.SequenceEqual(new[] { "128k" }) && last.Remaining == "0", "last available quality not identified");
        var exhausted = Bridge("resolve", Payload(), "flac", "flac,320k,128k");
        Check(exhausted.Code != 0 && exhausted.Tried.Length == 0, "exhausted list repeated a source request");

        var hires = Bridge("resolve", Payload("ok", "hires,320k"), "hires");
        Check(hires.Tried.SequenceEqual(new[] { "hires" }) && hires.Url[2] == "hires" && hires.Remaining == "1", "Hi-Res alias was not canonicalized");
        var excludedHires = Bridge("resolve", Payload("ok", "hires,320k"), "hires", "flac24bit");
        Check(excludedHires.Tried.SequenceEqual(new[] { "320k" }), "Hi-Res alias exclusion failed");

        var sourceFallback = Bridge("resolve", Payload("reject_lossless"), "flac");
        Check(sourceFallback.Code == 0 && sourceFallback.Tried.SequenceEqual(new[] { "flac", "320k" }), "source-side fallback lost attempt history");
        Check(sourceFallback.Remaining == "1", "source-side failures remained eligible");
        var all = Bridge("resolve", Payload("all"), "flac");
        Check(all.Code != 0 && all.Tried.SequenceEqual(new[] { "flac", "320k", "128k" }), "all failures repeated aliases or lost attempts");
        foreach (var fatal in new[] { "音源鉴权失败", "HTTP 401", "HTTP 429", "HTTP 403", "订阅过期", "timeout" })
        {
            var result = Bridge("resolve", Payload(fatal), "flac");
            Check(result.Code != 0 && result.Tried.SequenceEqual(new[] { "flac" }), "fatal source failure retried: " + fatal);
        }
    }

    static string WriteSource(string file, string platforms)
    {
        var path = Path.Combine(profile, file + ".js");
        var definitions = string.Join(",", platforms.Split(',').Select(p => p + ": {type:'music',actions:['musicUrl','musicSearch'],qualitys:['flac','320k']}"));
        File.WriteAllText(path, "/* @name " + file + " */\n" +
            "lx.on(lx.EVENT_NAMES.request, async ({source,action,info}) => action === 'musicSearch' ? " +
            "{list:[{songmid:'fixture-'+source,name:'测试歌曲',singer:'测试歌手',source:source,interval:'03:05'}],total:1} : 'https://fixture.invalid/" + file + "/'+source);" +
            "lx.send(lx.EVENT_NAMES.inited,{sources:{" + definitions + "}});");
        return path;
    }

    static void TestLibrary()
    {
        var initial = Bridge("source-options");
        var first = initial.Lines.Single(line => line[0] == "SOURCE")[1];
        var secondFile = WriteSource("source-b", "mg,kw,qs,custom_music");
        Check(Bridge("import-file", Encode(secondFile)).Code == 0, "second source import failed");
        var options = Bridge("source-options");
        var sources = options.Lines.Where(line => line[0] == "SOURCE").ToArray();
        Check(sources.Length == 2, "new import replaced old source");
        var second = sources.Single(line => line[3] == "1")[1];
        Check(options.Lines.Where(line => line[0] == "SITE" && line[1] == second).Select(line => line[2])
            .SequenceEqual(new[] { "migu", "kuwo", "qishui", "custom_music" }), "source platforms were not exposed correctly");
        Check(options.Lines.Any(line => line[0] == "SITE" && Decode(line[3]) == "汽水音乐"), "raw qs identifier leaked into labels");
        Check(Bridge("import-file", Encode(secondFile)).Code == 0, "duplicate source import failed");
        Check(Bridge("source-options").Lines.Count(line => line[0] == "SOURCE") == 2, "duplicate import created extra source");
        Check(Bridge("select-source", first).Code == 0, "switch to existing source failed");
        var search = Bridge("search", "migu", "track", Encode("测试"), "1", "3", "flac", second);
        Check(search.Code == 0 && search.Lines.Count(line => line[0] == "ITEM") == 1, "selected source did not provide search");
        var custom = Bridge("search", "custom_music", "track", Encode("测试"), "1", "3", "flac", second);
        Check(custom.Code == 0 && custom.Lines.Any(line => line[0] == "ITEM"), "source-declared custom platform was not searchable");
        Check(Bridge("source-options").Lines.Single(line => line[0] == "SOURCE" && line[3] == "1")[1] == second, "search did not persist source choice");
        var pinned = Bridge("resolve", Payload(), "flac", "-", first);
        Check(pinned.Code == 0 && pinned.Lines.Any(line => line[0] == "SOURCEID" && line[1] == first), "playback did not keep its original source");
        Check(Bridge("source-options").Lines.Single(line => line[0] == "SOURCE" && line[3] == "1")[1] == second, "pinned playback changed active source");
        Check(Bridge("select-source", "missing").Code != 0, "invalid selection succeeded");
        var bad = Path.Combine(profile, "bad.js");
        File.WriteAllText(bad, "throw new Error('fixture import failure');");
        Check(Bridge("import-file", Encode(bad)).Code != 0, "bad source was imported");
        Check(Bridge("source-options").Lines.Count(line => line[0] == "SOURCE") == 2, "failed import changed library");
        Check(Bridge("remove-source", first).Code == 0, "inactive source could not be removed");
        Check(Bridge("source-options").Lines.Single(line => line[0] == "SOURCE")[1] == second, "removing inactive source changed active source");

        var c = WriteSource("source-c", "tx");
        var d = WriteSource("source-d", "wy");
        var imports = Task.WhenAll(Task.Run(() => Bridge("import-file", Encode(c))), Task.Run(() => Bridge("import-file", Encode(d)))).Result;
        Check(imports.All(result => result.Code == 0), "concurrent imports failed");
        Check(Bridge("source-options").Lines.Count(line => line[0] == "SOURCE") == 3, "concurrent import lost a source");
        var sameTrack = Payload();
        var e = WriteSource("source-e", "tx");
        Check(Bridge("import-file", Encode(e)).Code == 0, "alternate same-platform source import failed");
        var resolvedE = Bridge("resolve", sameTrack, "flac");
        Check(resolvedE.Code == 0 && Decode(resolvedE.Url[1]) == "https://fixture.invalid/source-e/tx", "existing playlist item did not resolve with newly active source");
        var sourceC = Bridge("source-options").Lines.Single(line => line[0] == "SOURCE" && Decode(line[2]) == "source-c")[1];
        Check(Bridge("select-source", sourceC).Code == 0, "manager source switch failed");
        var resolvedC = Bridge("resolve", sameTrack, "flac");
        Check(resolvedC.Code == 0 && Decode(resolvedC.Url[1]) == "https://fixture.invalid/source-c/tx", "unchanged playlist payload retained old media/source address");
        var encrypted = File.ReadAllBytes(Path.Combine(profile, "online-source.json"));
        Check(encrypted[0] == 1 && !Encoding.UTF8.GetString(encrypted).Contains("fixture.invalid"), "source library was not protected");
        Check(Bridge("remove-source").Code == 0, "current source removal failed");
        Check(Bridge("source-options").Lines.Count(line => line[0] == "SOURCE" && line[3] == "1") == 1, "no valid active source after removal");
        var resolveOnly = WriteSource("resolve-only", "git,qs,tx");
        File.WriteAllText(resolveOnly, File.ReadAllText(resolveOnly).Replace("['musicUrl','musicSearch']", "['musicUrl']"));
        Check(Bridge("import-file", Encode(resolveOnly)).Code == 0, "resolve-only source import failed");
        var filtered = Bridge("source-options");
        var resolveOnlyId = filtered.Lines.Single(line => line[0] == "SOURCE" && line[3] == "1")[1];
        Check(filtered.Lines.Where(line => line[0] == "SITE" && line[1] == resolveOnlyId).Select(line => line[2]).SequenceEqual(new[] { "qq" }), "unsearchable source platforms were displayed");

        var originalProfile = profile;
        profile = Path.Combine(originalProfile, "legacy");
        Directory.CreateDirectory(profile);
        try
        {
            var legacy = new StoredOnlineSource { Script = File.ReadAllText(c), DisplayName = "旧音源",
                Descriptor = new LxSourceDescriptor { Sources = new Dictionary<string, List<string>> { ["tx"] = new List<string> { "flac" } } } };
            var legacyBytes = new byte[] { 0 }.Concat(Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(legacy))).ToArray();
            var file = Path.Combine(profile, "online-source.json");
            File.WriteAllBytes(file, legacyBytes);
            var legacyId = Bridge("source-options").Lines.Single(line => line[0] == "SOURCE")[1];
            Check(File.ReadAllBytes(file).SequenceEqual(legacyBytes), "reading legacy source unexpectedly rewrote it");
            Check(Bridge("import-file", Encode(d)).Code == 0, "legacy migration failed");
            var migrated = Bridge("source-options");
            Check(migrated.Lines.Count(line => line[0] == "SOURCE") == 2 && migrated.Lines.Any(line => line[0] == "SOURCE" && line[1] == legacyId), "migration lost legacy source/ID");
            var corrupt = new byte[] { 0, 123, 63 };
            File.WriteAllBytes(file, corrupt);
            Check(Bridge("import-file", Encode(d)).Code != 0 && File.ReadAllBytes(file).SequenceEqual(corrupt), "unreadable library was overwritten");
        }
        finally { profile = originalProfile; }
    }

    static void TestCompatibility()
    {
        const string script = @"
/**
 * @name Compatibility fixture
 * @version 1.2.3
 */
const b = lx.utils.buffer, c = lx.utils.crypto;
function check(ok, message) { if (!ok) throw new Error(message); }
check(lx.env === 'desktop', 'desktop environment');
check(c.md5('abc') === '900150983cd24fb0d6963f7d28e17f72', 'MD5 ASCII');
check(c.md5('中文') === 'a7bac2239fcdcb3a067903d8077c4a07', 'MD5 UTF8');
check(b.bufToString(b.from([0,127,128,255]), 'base64') === 'AH+A/w==', 'binary base64');
check(b.bufToString(b.from('AH+A/w==', 'base64'), 'hex') === '007f80ff', 'base64 decode');
check(b.bufToString(b.from('你好🎵'), 'base64') === '5L2g5aW98J+OtQ==', 'UTF8 four-byte encode');
check(b.bufToString(b.from('5L2g5aW98J+OtQ==', 'base64')) === '你好🎵', 'UTF8 four-byte decode');
check(b.bufToString(b.from(new Uint8Array([3,4,5]).subarray(1)), 'hex') === '0405', 'typed array offsets');
check(b.from('', 'hex').length === 0, 'empty hex');
const encrypted = c.aesEncrypt(b.from('00112233445566778899aabbccddeeff','hex'), 'aes-128-ecb', b.from('000102030405060708090a0b0c0d0e0f','hex'));
check(b.bufToString(encrypted,'hex') === '69c4e0d86a7b0430d8cdb78070b4c55a', 'AES known vector');
check(c.randomBytes(32).length === 32, 'random bytes');
lx.on(lx.EVENT_NAMES.request, async () => {
  const known = await lx.utils.zlib.inflate(b.from('eJzLSM3JyQcABiwCFQ==', 'base64'));
  check(b.bufToString(known) === 'hello', 'zlib fixture');
  const raw = b.from([0,255,128,1,2]);
  const restored = await lx.utils.zlib.inflate(await lx.utils.zlib.deflate(raw));
  check(b.bufToString(restored,'hex') === '00ff800102', 'zlib binary roundtrip');
  let rejected = false;
  try { await lx.utils.zlib.inflate(b.from('eJzLSM3JyQcABiwCFA==','base64')); } catch(e) { rejected = true; }
  check(rejected, 'corrupt zlib accepted');
  await new Promise(resolve => setTimeout(resolve, 5));
  return 'https://fixture.invalid/compatible';
});
lx.send(lx.EVENT_NAMES.inited,{sources:{tx:{type:'music',actions:['musicUrl'],qualitys:['flac']}}});
";
        using (var runtime = LxSourceRuntime.Create(script, "compatibility", TimeSpan.FromSeconds(10)))
        {
            Check(runtime.Descriptor.Name == "Compatibility fixture" && runtime.Descriptor.Version == "1.2.3", "script metadata was not preserved");
            var raw = runtime.InvokeAsync("musicUrl", "tx", "{}", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Check(raw.Contains("fixture.invalid/compatible"), "script API compatibility assertions failed");
        }
    }

    static void TestUpdates()
    {
        Func<string, string> json = version => "{\"tag_name\":\"" + version + "\",\"html_url\":\"https://github.com/lumiliya2009/foo_lxmusic/releases/tag/" + version + "\"}";
        Check(UpdateChecker.Parse(json("v0.1.10"), "0.1.9")?.Version == "v0.1.10", "numeric version comparison failed");
        Check(UpdateChecker.Parse(json("v0.1.7"), "0.1.7.0") == null, "same version reported as update");
        Check(UpdateChecker.Parse(json("v0.1.6"), "0.1.7") == null, "older version reported as update");
        Check(UpdateChecker.Parse(json("v0.1.8").Replace("{", "{\"prerelease\":true,"), "0.1.7") == null, "prerelease unexpectedly offered");
        bool rejected = false;
        try { UpdateChecker.Parse(json("v0.1.8").Replace("github.com/lumiliya2009/foo_lxmusic", "github.com/elsewhere/project"), "0.1.7"); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "foreign release URL accepted");
    }

    static void TestSearchPagination()
    {
        var first = new OnlinePage().WithPagination(1, 30, 65, 30);
        Check(first.TotalKnown && first.TotalPages == 3 && first.HasNext, "first-page total was lost");
        var last = new OnlinePage().WithPagination(3, 30, 65, 5);
        Check(last.TotalPages == 3 && !last.HasNext && last.Total == 65, "last-page calculation failed");
        Check(new OnlinePage().WithPagination(1, 30, 30, 30).TotalPages == 1, "exact page boundary added an extra page");
        var unknown = new OnlinePage().WithPagination(2, 30, null, 30);
        Check(!unknown.TotalKnown && unknown.TotalPages == null && unknown.HasNext, "unknown total was fabricated");
        var unknownLast = new OnlinePage().WithPagination(3, 30, null, 5);
        Check(!unknownLast.HasNext && unknownLast.TotalPages == null, "short unknown page invented a total");
        var invalid = new OnlinePage().WithPagination(2, 30, 10, 30);
        Check(!invalid.TotalKnown && invalid.TotalPages == null && invalid.HasNext, "inconsistent total blocked pagination");
        var max = new OnlinePage().WithPagination(1, 30, int.MaxValue, 30);
        Check(max.TotalPages == 71582789, "page count arithmetic overflowed");
        var pageCount = OnlineMusicRepository.ParseNativeSearchResult("{\"list\":[{\"id\":\"a\",\"name\":\"A\"}],\"allPage\":4}",
            OnlinePlatform.Qq, "tx", 2, 30, new List<string> { "flac" });
        Check(!pageCount.TotalKnown && pageCount.TotalPages == 4 && pageCount.HasNext, "source page-count metadata was ignored");
        var more = OnlineMusicRepository.ParseNativeSearchResult("{\"list\":[{\"id\":\"a\",\"name\":\"A\"}],\"hasNext\":true}",
            OnlinePlatform.Qq, "tx", 2, 30, new List<string> { "flac" });
        Check(more.HasNext && more.TotalPages == null, "source explicit hasNext was ignored");
        var originalProfile = profile;
        profile = Path.Combine(profile, "search-pages");
        Directory.CreateDirectory(profile);
        try
        {
            var file = Path.Combine(profile, "pagination.js");
            File.WriteAllText(file, @"
lx.on(lx.EVENT_NAMES.request, ({info}) => {
  if(info.pagesize !== 30 || info.limit !== 30 || info.offset !== (info.page - 1) * 30) throw new Error('page arguments mismatch');
  const total = 65, start = (info.page - 1) * 30;
  const list = Array.from({length:Math.max(0,Math.min(30,total-start))}, (_,i) => ({id:String(start+i+1),name:'歌曲'+(start+i+1),singer:'歌手'}));
  if(info.keyword === 'unknown') return {list};
  if(info.keyword === 'pages-only') return {list,allPage:3};
  return {list,total};
});
lx.send(lx.EVENT_NAMES.inited,{sources:{tx:{type:'music',actions:['musicUrl','musicSearch'],qualitys:['flac']}}});");
            Check(Bridge("import-file", Encode(file)).Code == 0, "pagination fixture import failed");
            var pages = new List<Result>();
            for (int number = 1; number <= 3; ++number)
            {
                var result = Bridge("search", "qq", "track", Encode("known"), number.ToString(), "30", "flac");
                Check(result.Code == 0, "pagination source request failed");
                var metadata = result.Lines.Single(line => line[0] == "PAGE");
                Check(metadata.SequenceEqual(new[] { "PAGE", number.ToString(), "30", "3", "65", number < 3 ? "1" : "0" }), "bridge discarded page metadata");
                Check(result.Lines.Count(line => line[0] == "ITEM") == (number < 3 ? 30 : 5), "page size was not preserved");
                pages.Add(result);
            }
            var ids = pages.SelectMany(result => result.Lines.Where(line => line[0] == "ITEM").Select(line => line[1])).ToList();
            Check(ids.Distinct().Count() == 65, "page requests repeated the first 30 results");
            var noTotal = Bridge("search", "qq", "track", Encode("unknown"), "2", "30", "flac");
            Check(noTotal.Lines.Single(line => line[0] == "PAGE").SequenceEqual(new[] { "PAGE", "2", "30", "-1", "-1", "1" }), "unknown page count was not exported honestly");
            var pagesOnly = Bridge("search", "qq", "track", Encode("pages-only"), "3", "30", "flac");
            Check(pagesOnly.Lines.Single(line => line[0] == "PAGE").SequenceEqual(new[] { "PAGE", "3", "30", "3", "-1", "0" }), "page-only totals were lost");
            var empty = Bridge("search", "qq", "track", Encode("known"), "4", "30", "flac");
            Check(empty.Code == 0 && !empty.Lines.Any(line => line[0] == "ITEM"), "empty page was filled with unrelated results");
            Check(Bridge("search", "qq", "track", Encode("known"), int.MaxValue.ToString(), "30", "flac").Code != 0, "overflowing page number was accepted");
        }
        finally { profile = originalProfile; }
    }

    static void TestCatalogs(bool live)
    {
        var kuwo = ExtendedCatalog.ParseKuwo("{\"TOTAL\":31,\"abslist\":[{\"MUSICRID\":\"MUSIC_123\",\"SONGNAME\":\"A&amp;B\",\"ARTIST\":\"歌手\",\"DURATION\":201,\"N_MINFO\":\"level:p,bitrate:320,format:mp3,size:8;level:ff,bitrate:2000,format:flac,size:25\"}]}", 1, 30);
        Check(kuwo.HasNext && kuwo.Tracks.Single().Title == "A&B" && kuwo.Tracks[0].DurationMs == 201000, "Kuwo metadata/pagination incorrect");
        var kwInfo = JObject.Parse(kuwo.Tracks[0].MusicInfoJson);
        Check((string)kwInfo["songmid"] == "123" && (string)kwInfo["source"] == "kw" && kwInfo["_types"]["320k"] != null, "Kuwo script payload incorrect");
        var migu = ExtendedCatalog.ParseMigu("{\"code\":\"000000\",\"songResultData\":{\"totalCount\":1,\"resultList\":[[{\"songId\":\"123\",\"copyrightId\":\"copyright-321\",\"name\":\"歌曲\",\"duration\":185,\"singerList\":[{\"name\":\"歌手\"}],\"audioFormats\":[{\"formatType\":\"ZQ24\"}]}]]}}", 1, 30);
        var mgInfo = JObject.Parse(migu.Tracks.Single().MusicInfoJson);
        Check((string)mgInfo["copyrightId"] == "copyright-321" && (string)mgInfo["songmid"] == "123" && (string)mgInfo["interval"] == "03:05", "Migu playback IDs/duration lost");
        Check(migu.Tracks[0].AvailableQualities.Single() == OnlineQuality.HiRes && !migu.HasNext, "Migu quality/pagination incorrect");
        if (live)
        {
            TestSourceProxy();
            foreach (var platform in new[] { OnlinePlatform.Kuwo, OnlinePlatform.Migu })
            {
                var results = ExtendedCatalog.Search(platform, "张学友", 1, 3);
                Check(results.Tracks.Count > 0 && results.Tracks.All(track => track.Platform == platform && !string.IsNullOrWhiteSpace(track.Id)), "live catalog returned no valid songs: " + platform.Id);
                Console.WriteLine(platform.Label + ": " + results.Tracks.Count + " catalog tracks");
            }
        }
    }

    static void TestSourceProxy()
    {
        var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var savedProxy = WebRequest.DefaultWebProxy;
        try
        {
            WebRequest.DefaultWebProxy = new WebProxy("http://127.0.0.1:" + ((IPEndPoint)proxy.LocalEndpoint).Port);
            var reply = Task.Run(async () =>
            {
                using (var client = await proxy.AcceptTcpClientAsync())
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 8000;
                    var header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n"))
                    {
                        var next = stream.ReadByte();
                        if (next < 0 || header.Length > 16000) throw new Exception("invalid proxy request");
                        header.Append((char)next);
                    }
                    Check(header.ToString().StartsWith("GET http://music.163.com/lx-fixture"), "script did not send request through configured proxy");
                    const string body = "{\"marker\":\"fixture\"}";
                    var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body);
                    stream.Write(bytes, 0, bytes.Length);
                }
            });
            const string script = @"
lx.on(lx.EVENT_NAMES.request, () => new Promise((resolve,reject) => {
  lx.request('http://music.163.com/lx-fixture', {timeout:5000}, (error,response) => {
    if(error) return reject(error);
    if(response.body.marker !== 'fixture') return reject(new Error('proxy response missing'));
    resolve('https://fixture.invalid/proxy');
  });
}));
lx.send(lx.EVENT_NAMES.inited,{sources:{wy:{type:'music',actions:['musicUrl'],qualitys:['128k']}}});";
            using (var runtime = LxSourceRuntime.Create(script, "proxy-test", TimeSpan.FromSeconds(10)))
            {
                var result = runtime.InvokeAsync("musicUrl", "wy", "{}", TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
                Check(result.Contains("fixture.invalid/proxy"), "script request did not honor system proxy for music provider");
            }
            if (!reply.Wait(1000)) throw new Exception("proxy handler did not complete");
        }
        finally { WebRequest.DefaultWebProxy = savedProxy; proxy.Stop(); }
    }

    static int Main(string[] args)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        profile = Path.Combine(tempRoot, "lx-quality-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            TestConsole();
            TestOrdering();
            TestBridge();
            TestLibrary();
            TestCompatibility();
            TestUpdates();
            TestSearchPagination();
            TestCatalogs(args.Contains("--live"));
            Console.WriteLine(checks + " quality/bridge regression checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (!Path.GetFullPath(profile).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe fixture cleanup path");
            Directory.Delete(profile, true);
        }
    }
}
