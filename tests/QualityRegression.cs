using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LxMusic.Bridge.Online;

static class QualityRegression
{
    static int checks;
    static string profile;
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
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

    static int Main()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        profile = Path.Combine(tempRoot, "lx-quality-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            TestOrdering();
            TestBridge();
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
