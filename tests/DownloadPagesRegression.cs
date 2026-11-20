using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LxMusic.Bridge.Online;

static class DownloadPagesRegression
{
    static int checks;
    static string root, profile, source;
    static readonly byte[] Existing = Encoding.ASCII.GetBytes("fLaC_fixture_group_1");
    static void Check(bool value, string reason) { ++checks; if (!value) throw new Exception(reason); }
    static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Decode(string text) { text = text.Replace('-', '+').Replace('_', '/'); return Encoding.UTF8.GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='))); }

    sealed class MediaServer : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly Task worker;
        readonly System.Collections.Concurrent.ConcurrentBag<Task> handlers = new System.Collections.Concurrent.ConcurrentBag<Task>();
        public string Url { get; }
        public int Requests;
        public int Delay, Active, Peak;
        public MediaServer()
        {
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = Task.Run(() => {
                try
                {
                    for (;;)
                    {
                        var client = listener.AcceptTcpClient();
                        handlers.Add(Task.Factory.StartNew(() => Handle(client), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
        }
        void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 5000;
                    var request = new StringBuilder();
                    while (!request.ToString().EndsWith("\r\n\r\n"))
                    {
                        var next = stream.ReadByte(); if (next < 0) break;
                        request.Append((char)next);
                        if (request.Length > 16384) throw new IOException("oversized fixture request");
                    }
                    var path = request.ToString().Split(' ')[1];
                    var id = int.Parse(Path.GetFileNameWithoutExtension(path));
                    Interlocked.Increment(ref Requests);
                    var active = Interlocked.Increment(ref Active);
                    for (;;) { var peak = Volatile.Read(ref Peak); if (peak >= active || Interlocked.CompareExchange(ref Peak, active, peak) == peak) break; }
                    var fail = path.StartsWith("/fail/") && id == 3;
                    if (Delay > 0) Thread.Sleep(fail ? 40 : Delay);
                    Interlocked.Decrement(ref Active);
                    var bytes = Encoding.ASCII.GetBytes("fLaC_fixture_group_" + id % 4);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (fail ? "503 Unavailable" : "200 OK") + "\r\nContent-Type: audio/flac\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
        public void Dispose() { listener.Stop(); worker.Wait(5000); Task.WaitAll(handlers.ToArray(), 5000); }
    }

    static string[][] Bridge(int expected, params string[] args)
    {
        var start = new ProcessStartInfo(typeof(OnlineMusicRepository).Assembly.Location,
            string.Join(" ", new[] { args[0], Encode(profile) }.Concat(args.Skip(1))))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45000)) { process.Kill(); throw new Exception("batch download test timed out"); }
            Check(process.ExitCode == expected && error.Result.Length == 0, "bridge command failed: " + output.Result + error.Result);
            return output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).ToArray();
        }
    }

    static string Download(string mode, bool skip, bool cleanup, out string directory, int hint = 3, int workers = 1, string pattern = "{序号} - {曲名}")
    {
        directory = Path.Combine(root, mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "existing.flac"), Existing);
        var lines = Bridge(0, "download-search", Encode(directory), "flac", Encode(pattern),
            skip ? "1" : "0", cleanup ? "1" : "0", source, "qq", "track", Encode(mode), hint.ToString(), "30", workers.ToString());
        var done = lines.Single(line => line[0] == "DONE");
        Check(File.ReadAllBytes(Path.Combine(directory, "existing.flac")).SequenceEqual(Existing), "existing audio changed");
        Check(!Directory.GetFiles(directory, "*.part", SearchOption.AllDirectories).Any(), "completed job left partial files");
        return Decode(done[3]);
    }

    static void Run()
    {
        using (var server = new MediaServer())
        {
            var script = Path.Combine(profile, "pages.js");
            File.WriteAllText(script, @"
let mode = '', resolved = 0;
lx.on(lx.EVENT_NAMES.request, async ({action,info}) => {
  if (action === 'musicSearch') {
    mode = info.keyword;
    if (info.limit !== 30 || info.pagesize !== 30 || info.offset !== (info.page-1)*30) throw new Error('page parameters mismatch');
    if (mode === 'page-error' && info.page === 2) throw new Error('page fixture failure');
    if (mode === 'unknown-errors' && info.page >= 2) throw new Error('unknown page fixture failure');
    if (mode === 'known' && info.page > 1 && resolved < (info.page-1)*30) throw new Error('page fetched before previous page downloaded');
    const page = mode === 'repeat' ? 1 : info.page;
    const offset = (page-1)*30;
    const list = Array.from({length:Math.max(0,Math.min(30,65-offset))}, (_,i) => ({id:String(offset+i+1),name:mode === 'same-names' ? 'Same' : 'Song '+String(offset+i+1).padStart(3,'0'),singer:'fixture'}));
    return mode.startsWith('unknown') ? {list} : {list,total:mode === 'inflated' ? 600 : 65};
  }
  const id = String(info.musicInfo.songmid || info.musicInfo.songId || info.musicInfo.id);
  if (mode === 'track-error' && id === '2') throw new Error('track fixture failure');
  resolved++;
  return '" + server.Url + @"/'+(mode === 'transfer-error' ? 'fail/' : '')+id+'.flac';
});
lx.send(lx.EVENT_NAMES.inited,{sources:{tx:{type:'music',actions:['musicUrl','musicSearch'],qualitys:['flac']}}});
", new UTF8Encoding(false));
            Bridge(0, "import-file", Encode(script));
            source = Bridge(0, "source-options").Single(line => line[0] == "SOURCE")[1];
            var otherScript = Path.Combine(profile, "other.js");
            File.WriteAllText(otherScript, "lx.on(lx.EVENT_NAMES.request,async()=>{throw new Error('wrong active source')});lx.send(lx.EVENT_NAMES.inited,{sources:{tx:{type:'music',actions:['musicUrl','musicSearch'],qualitys:['flac']}}});");
            Bridge(0, "import-file", Encode(otherScript));
            var selected = Bridge(0, "source-options").Single(line => line[0] == "SOURCE" && line[3] == "1")[1];
            Check(selected != source, "source fixture failed to switch");

            var summary = Download("known", true, false, out var directory);
            Check(summary.Contains("成功 65 首") && summary.Contains("已读取 3 页"), "known page download incomplete: " + summary);
            Check(File.Exists(Path.Combine(directory, "01 - Song 001.flac")) && File.Exists(Path.Combine(directory, "65 - Song 065.flac")), "page numbering reset or final page missing");
            Check(Directory.GetFiles(directory, "*.flac").Length == 66, "cleanup-disabled download removed duplicates");
            Check(Bridge(0, "source-options").Single(line => line[0] == "SOURCE" && line[3] == "1")[1] == selected, "all-pages task changed active source");

            var before = server.Requests;
            summary = Download("known", true, true, out directory);
            Check(server.Requests - before == 65, "duplicates were skipped before download");
            Check(summary.Contains("删除 62 个重复文件") && Directory.GetFiles(directory, "*.flac").Length == 4, "post-download cleanup failed across pages: " + summary);

            summary = Download("page-error", true, false, out directory);
            Check(summary.Contains("成功 35 首") && summary.Contains("失败 1 页") && File.Exists(Path.Combine(directory, "61 - Song 061.flac")), "failed page did not skip to next page");
            summary = Download("page-error", false, false, out directory);
            Check(summary.StartsWith("已停止下载") && summary.Contains("成功 30 首") && Directory.GetFiles(directory, "*.flac").Length == 31, "stop-on-page-failure continued downloading");
            summary = Download("track-error", true, false, out directory);
            Check(summary.Contains("成功 64 首，歌曲失败 1 首") && File.Exists(Path.Combine(directory, "03 - Song 003.flac")), "skip-track-failure failed");
            summary = Download("track-error", false, true, out directory);
            Check(summary.StartsWith("已停止下载") && summary.Contains("成功 1 首，歌曲失败 1 首") && summary.Contains("删除 1 个重复文件"), "stop-on-track-failure or partial cleanup failed");
            summary = Download("unknown", true, false, out directory, -1);
            Check(summary.Contains("成功 65 首") && summary.Contains("已读取 3 页"), "unknown page count failed");
            summary = Download("inflated", true, false, out directory, 20);
            Check(summary.Contains("成功 65 首") && summary.Contains("已读取 4 页"), "inflated total did not stop on empty page");
            summary = Download("repeat", true, false, out directory);
            Check(summary.StartsWith("已停止下载") && summary.Contains("重复返回同一页") && summary.Contains("成功 30 首"), "repeating page did not stop safely");
            summary = Download("unknown-errors", true, false, out directory, -1);
            Check(summary.StartsWith("已停止下载") && summary.Contains("连续 3 页") && summary.Contains("失败 3 页"), "unknown-total failures looped indefinitely");

            server.Delay = 180; server.Peak = 0;
            summary = Download("known", true, true, out directory, workers: 3);
            Check(server.Peak == 3, "requested three parallel transfers but observed " + server.Peak);
            Check(summary.Contains("成功 65 首") && summary.Contains("删除 62 个重复文件"), "parallel paging/cleanup failed: " + summary);
            server.Peak = 0;
            summary = Download("same-names", true, false, out directory, workers: 4, pattern: "{曲名}");
            Check(server.Peak == 4 && summary.Contains("成功 65 首"), "four-worker collision job failed: " + summary);
            Check(Directory.GetFiles(directory, "Same*.flac").Length == 65 && File.Exists(Path.Combine(directory, "Same (65).flac")), "concurrent same-name files overwritten or lost");
            server.Peak = 0; before = server.Requests;
            summary = Download("transfer-error", false, false, out directory, workers: 3);
            Check(summary.StartsWith("已停止下载") && summary.Contains("歌曲失败 1 首"), "parallel failure not reported: " + summary);
            Check(server.Requests - before < 65 && server.Peak <= 3, "parallel failure continued batch or exceeded limit");
            server.Delay = 0;
            Bridge(1, "download-search", Encode(directory), "flac", Encode("{曲名}"), "1", "0", source, "qq", "artist", Encode("known"), "3", "30");
            Bridge(1, "download-search", Encode(directory), "flac", Encode("{曲名}"), "bad", "0", source, "qq", "track", Encode("known"), "3", "30");
            foreach (var workers in new[] { "0", "17", "bad" })
                Bridge(1, "download-search", Encode(directory), "flac", Encode("{曲名}"), "1", "0", source, "qq", "track", Encode("known"), "3", "30", workers);
        }
    }

    static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "lx-download-pages-" + Guid.NewGuid().ToString("N"));
        profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
        try { Run(); Console.WriteLine(checks + " all-pages download checks passed"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (!Path.GetFullPath(root).StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe cleanup path");
            Directory.Delete(root, true);
        }
    }
}
