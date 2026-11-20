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

static class AlbumDownloadRegression
{
    static int checks;
    static string root;
    static void Check(bool result, string message) { checks++; if (!result) throw new Exception(message); }
    static void Fails(Action action, string message)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        Check(failed, message);
    }
    static OnlineCollection Album(string title = "测试专辑", string artist = "测试歌手") => new OnlineCollection
    {
        Kind = OnlineCollectionKind.Album, Platform = OnlinePlatform.Qq, Id = "album-id", Title = title,
        Subtitle = artist, ArtworkUrl = "https://art.invalid/cover"
    };
    static OnlineTrack Track(int number) => new OnlineTrack { Platform = OnlinePlatform.Qq, Id = number.ToString(), Title = "曲目" + number, ArtworkUrl = "https://art.invalid/fallback" };
    static OnlineSearchPage Page(int first, int count, bool more = false) => new OnlineSearchPage
    {
        Tracks = Enumerable.Range(first, count).Select(Track).ToList(), HasNext = more
    };
    static OnlineAlbumDownload Prepare(OnlineCollection album) => OnlineAlbumDownload.Prepare(album, root, (_, __) => Page(1, 2));

    static void TestNaming()
    {
        var track = new OnlineTrack { Title = "李香兰", Artist = "张学友", Album = "梦中的你" };
        var media = new ResolvedOnlineMedia { Url = "https://media.invalid/audio.flac", Quality = OnlineQuality.Lossless, SourceQuality = "flac" };
        Func<string, string> name = template => DownloadFileNaming.BuildFileName(template, track, media, 1, 12);
        Check(name(DownloadFileNaming.DefaultTemplate) == "01 - 李香兰 - 张学友.flac", "default custom filename wrong");
        Check(name("{曲名} - {艺术家}") == "李香兰 - 张学友.flac", "title/artist order wrong");
        Check(name("{艺术家} - {文件名}") == "张学友 - 李香兰.flac", "filename alias wrong");
        Check(name("{专辑名}_{序号}_{曲名}_{音质}") == "梦中的你_01_李香兰_flac.flac", "custom separators or fields lost");
        Check(name("收藏 [{序号}] {曲名}") == "收藏 [01] 李香兰.flac", "literal prefix changed");
        Check(DownloadFileNaming.BuildFileName("{序号}", track, media, 102, 102) == "102.flac", "number width not expanded");
        Check(DownloadFileNaming.BuildFileName("{序号}", track, media, 1, 102) == "001.flac", "numbers not padded to total width");
        Check(OnlineDownloader.BuildFileName(track, media, "{曲名}", 1, 1) == "李香兰.flac", "single-track naming not wired");
        var album = Prepare(Album());
        Check(album.TrackFileName(1, media, "{专辑名} - {序号} - {曲名}") == "测试专辑 - 02 - 曲目2.flac", "album naming not wired");
        Check(Prepare(Album("另一专辑")).TrackFileName(0, media, "{序号} {曲名}") == "01 曲目1.flac", "album numbering did not restart");
        track.Title = "{艺术家} 曲名";
        Check(name("{曲名}") == "{艺术家} 曲名.flac", "metadata was expanded as a second template");
        track.Title = "../CON:test\\song?\n";
        var safe = name("{曲名}");
        Check(Path.GetFileName(safe) == safe && safe.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "unsafe metadata became a path");
        Check(Path.GetFullPath(Path.Combine(root, safe)).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "filename escaped download root");
        foreach (var reserved in new[] { "CON", "nul", "AUX.wav", "COM1", "LPT³" })
        {
            track.Title = reserved;
            Check(name("{曲名}").StartsWith("_"), "reserved Windows name accepted: " + reserved);
        }
        track.Title = new string('歌', 119) + "😀尾";
        var shortened = Path.GetFileNameWithoutExtension(name("{曲名}"));
        Check(shortened.Length <= 120 && !char.IsHighSurrogate(shortened.Last()), "filename truncation split an emoji");
        track.Title = null; track.Artist = " "; track.Album = null;
        Check(name("{曲名} - {艺术家} - {专辑名}") == "未命名歌曲 - 未知艺术家 - 未知专辑.flac", "missing metadata produced an empty name");
        foreach (var invalid in new[] { "", " ", "{未知字段}", "{曲名", "曲名}", "{{曲名}}", new string('x', 257) })
            Fails(() => DownloadFileNaming.ValidateTemplate(invalid), "invalid template accepted");
        Fails(() => DownloadFileNaming.BuildFileName("{序号}", track, media, 0, 2), "zero sequence accepted");
        Check(DownloadFileNaming.Preview("{序号}-{曲名}", OnlineQuality.Lossless) == "01-示例歌曲.flac", "FLAC preview wrong");
        Check(DownloadFileNaming.Preview("{曲名}", OnlineQuality.High).EndsWith(".mp3"), "MP3 preview wrong");
    }

    static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Decode(string text) { text = text.Replace('-', '+').Replace('_', '/'); return Encoding.UTF8.GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='))); }
    static string[][] Bridge(string profile, int expectedExit, params string[] args)
    {
        var command = new[] { args[0], Encode(profile) }.Concat(args.Skip(1));
        var start = new ProcessStartInfo(typeof(OnlineMusicRepository).Assembly.Location, string.Join(" ", command))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("download bridge test timed out"); }
            Check(process.ExitCode == expectedExit && error.Result.Length == 0, "unexpected bridge result: " + output.Result + error.Result);
            return output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).ToArray();
        }
    }

    static void TestNamingBridge()
    {
        var profile = Path.Combine(root, "naming-profile");
        var preview = Bridge(profile, 0, "download-name-preview", Encode("{序号} - {曲名} - {艺术家}"), "flac");
        Check(preview.Length == 1 && preview[0][0] == "NAME" && Decode(preview[0][1]) == "01 - 示例歌曲 - 示例艺术家.flac", "preview protocol wrong");
        Check(!File.Exists(Path.Combine(profile, "online-source.json")), "preview required or created a source");
        var badPreview = Bridge(profile, 1, "download-name-preview", Encode("{unknown}"), "flac");
        Check(badPreview[0][0] == "ERROR" && Decode(badPreview[0][1]).Contains("命名字段"), "invalid preview lacks useful error");
        var directory = Path.Combine(root, "named-transfers");
        Bridge(profile, 1, "download-batch", Encode("missing-list"), Encode(directory), "flac", Encode("{unknown}"));
        Check(!Directory.Exists(directory), "invalid template created download directory");
        var script = Path.Combine(profile, "download-test.js");
        File.WriteAllText(script, @"
lx.on(lx.EVENT_NAMES.request, async ({info}) => {
  if (info.musicInfo.songId === 'fail') throw new Error('fixture failure');
  return info.musicInfo.fixtureUrl;
});
lx.send(lx.EVENT_NAMES.inited,{sources:{tx:{type:'music',actions:['musicUrl'],qualitys:['flac']}}});
", new UTF8Encoding(false));
        Bridge(profile, 0, "import-file", Encode(script));
        var bytes = Encoding.ASCII.GetBytes("fLaC_named_transfer_fixture");
        using (var server = new Server(bytes))
        {
            Func<string, string> payload = id => {
                var fields = new[] { "1", "track", "qq", "tx", "fixture", id, "曲名:二", "歌手/甲", "测试专辑", "1000", "", "flac",
                    "{\"songId\":\"" + id + "\",\"fixtureUrl\":\"" + server.Url + "\"}", "flac", "" };
                return Encode(string.Concat(fields.Select(value => Encoding.UTF8.GetByteCount(value) + ":" + value)));
            };
            var list = Path.Combine(profile, "downloads.txt");
            File.WriteAllLines(list, new[] { payload("fail"), payload("ok"), payload("ok") });
            Directory.CreateDirectory(directory);
            var original = Path.Combine(directory, "02 - 曲名_二 - 歌手_甲.flac");
            File.WriteAllText(original, "keep");
            var result = Bridge(profile, 0, "download-batch", Encode(list), Encode(directory), "flac", Encode(DownloadFileNaming.DefaultTemplate));
            var done = result.Single(line => line[0] == "DONE");
            Check(done[1] == "1" && done[2] == "1", "batch deduplication or failure count wrong");
            Check(File.ReadAllText(original) == "keep", "custom naming overwrote an existing file");
            Check(File.ReadAllBytes(Path.Combine(directory, "02 - 曲名_二 - 歌手_甲 (2).flac")).SequenceEqual(bytes), "failed preceding track changed numbering or template lost in batch");
            Check(Directory.GetFiles(directory).Length == 2, "extra or partial named download left behind");
        }
    }

    static void TestPlanningAndCovers()
    {
        var pages = 0;
        var plan = OnlineAlbumDownload.Prepare(Album(), root, (_, page) => { pages++; return page == 1 ? Page(1, 100, true) : Page(100, 3); });
        Check(pages == 2 && plan.Tracks.Count == 102, "album pagination or duplicate handling failed");
        Check(plan.Tracks[100].Id == "101" && plan.Tracks[101].Id == "102", "album sequence changed");
        Check(plan.Tracks.All(track => track.Album == "测试专辑"), "missing album names were not filled");
        var media = new ResolvedOnlineMedia { Url = "https://media.invalid/file.flac?token=x#part", MimeType = "audio/x-ogg" };
        Check(plan.TrackFileName(0, media) == "001 - 曲目1.flac", "first numbered filename wrong");
        Check(plan.TrackFileName(101, media) == "102 - 曲目102.flac", "last numbered filename wrong");
        Check(OnlineDownloader.FileExtension("https://media.invalid/no-extension", "audio/aac") == "aac", "AAC extension wrong");

        var sentinel = Path.Combine(plan.DirectoryPath, "keep.txt");
        File.WriteAllText(sentinel, "existing album");
        var second = Prepare(Album());
        Check(second.DirectoryPath != plan.DirectoryPath, "existing album directory reused");
        Check(File.ReadAllText(sentinel) == "existing album", "existing album overwritten");
        var escaped = Prepare(Album("../../escape\\album", "artist:bad"));
        Check(Path.GetFullPath(escaped.DirectoryPath).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "album escaped output directory");
        Check(!Path.GetFileName(escaped.DirectoryPath).Contains(':'), "invalid name characters retained");
        Check(Path.GetFileName(Prepare(Album("CON", "")).DirectoryPath) == "_CON", "reserved Windows name was not escaped");

        var before = Directory.GetDirectories(root).Length;
        Fails(() => OnlineAlbumDownload.Prepare(Album(), root, (_, __) => Page(1, 0)), "empty album accepted");
        Fails(() => OnlineAlbumDownload.Prepare(Album(), root, (_, __) => Page(1, 1, true)), "repeated pages accepted");
        Fails(() => OnlineAlbumDownload.Prepare(Album(), root, (_, __) => Page(1, 0, true)), "empty continuation accepted");
        Fails(() => OnlineAlbumDownload.Prepare(Album(), root, (_, page) => page == 1 ? Page(1, 2, true) : throw new IOException("page failed")), "partial album accepted");
        Check(Directory.GetDirectories(root).Length == before, "failed album created a partial output directory");

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aDCsAAAAASUVORK5CYII=");
        int fetches = 0;
        var cover = second.SaveArtwork(url => { fetches++; return url.EndsWith("fallback") ? png : Encoding.UTF8.GetBytes("<html>expired</html>"); });
        Check(fetches == 3 && Path.GetFileName(cover) == "cover.png", "cover fallback failed");
        Check(File.ReadAllBytes(cover).SequenceEqual(png), "cover bytes changed");
        var another = second.SaveArtwork(_ => png);
        Check(another != cover && File.ReadAllBytes(cover).SequenceEqual(png), "existing cover overwritten");
        Fails(() => second.SaveArtwork(_ => Encoding.UTF8.GetBytes("{\"error\":403}")), "JSON saved as a cover");
        Fails(() => OnlineAlbumDownload.SaveArtworkBytes(second.DirectoryPath, new byte[0]), "empty cover accepted");
        Fails(() => OnlineAlbumDownload.SaveArtworkBytes(second.DirectoryPath, new byte[8 * 1024 * 1024 + 1]), "oversize cover accepted");
        Check(plan.Tracks.Count == 102 && File.Exists(sentinel), "cover failure affected album data");
    }

    sealed class Server : IDisposable
    {
        readonly TcpListener listener;
        readonly Task worker;
        internal readonly string Url;
        internal Server(byte[] body, int? declaredLength = null, int status = 200)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/audio.flac";
            worker = Task.Run(() =>
            {
                try
                {
                    using (var client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 5000;
                        var request = new StringBuilder();
                        while (!request.ToString().EndsWith("\r\n\r\n"))
                        {
                            int value = stream.ReadByte();
                            if (value < 0) return;
                            request.Append((char)value);
                        }
                        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Type: audio/flac\r\nContent-Length: " + (declaredLength ?? body.Length) + "\r\nConnection: close\r\n\r\n");
                        stream.Write(header, 0, header.Length);
                        stream.Write(body, 0, body.Length);
                    }
                }
                catch (SocketException) { }
                catch (IOException) { }
            });
        }
        public void Dispose() { listener.Stop(); worker.Wait(6000); }
    }

    static void TestTransfers()
    {
        var directory = Path.Combine(root, "transfers");
        var data = Encoding.ASCII.GetBytes("fLaC_test_transfer_payload");
        long received = 0;
        using (var server = new Server(data))
        {
            OnlineDownloader.Download(server.Url, directory, "audio.flac", (count, total) => received = count, CancellationToken.None);
            Check(File.ReadAllBytes(Path.Combine(directory, "audio.flac")).SequenceEqual(data) && received == data.Length, "download bytes/progress wrong");
        }
        using (var server = new Server(data))
            Fails(() => OnlineDownloader.Download(server.Url, directory, "audio.flac", null, CancellationToken.None), "existing audio overwritten");
        Check(File.ReadAllBytes(Path.Combine(directory, "audio.flac")).SequenceEqual(data), "existing audio changed");
        using (var server = new Server(data, data.Length + 100))
            Fails(() => OnlineDownloader.Download(server.Url, directory, "truncated.flac", null, CancellationToken.None), "truncated download committed");
        using (var server = new Server(new byte[0]))
            Fails(() => OnlineDownloader.Download(server.Url, directory, "empty.flac", null, CancellationToken.None), "empty download committed");
        using (var server = new Server(data, status: 500))
            Fails(() => OnlineDownloader.Download(server.Url, directory, "failed.flac", null, CancellationToken.None), "HTTP failure committed");
        using (var server = new Server(data))
        using (var cancel = new CancellationTokenSource())
            Fails(() => OnlineDownloader.Download(server.Url, directory, "cancelled.flac", (_, __) => cancel.Cancel(), cancel.Token), "cancelled download committed");
        Check(Directory.GetFiles(directory).Length == 1, "failed/cancelled downloads left partial files");
    }

    static void TestLiveAlbum()
    {
        var results = OnlineCollectionSearch.Search(OnlinePlatform.Qq, OnlineSearchType.Albums, "梦中的你 张学友", 1, 10);
        var album = results.Collections.First(item => item.Title.Contains("梦中的你") && (item.Subtitle ?? "").Contains("张学友"));
        var plan = OnlineAlbumDownload.Prepare(album, root, OnlineCollectionSearch.AlbumTracks);
        Check(plan.Tracks.Count > 0, "live album empty");
        var cover = plan.SaveArtwork();
        using (var image = System.Drawing.Image.FromFile(cover))
        {
            Check(image.Width > 0 && image.Height > 0, "live cover cannot be decoded");
            Console.WriteLine("Live album: " + album.Subtitle + " - " + album.Title + "; tracks=" + plan.Tracks.Count + "; cover=" + image.Width + "x" + image.Height + " " + Path.GetExtension(cover));
        }
    }

    static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "lx-album-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestPlanningAndCovers();
            TestNaming();
            TestNamingBridge();
            TestTransfers();
            if (args.Contains("--live")) TestLiveAlbum();
            Console.WriteLine(checks + " album/cover/download regression checks passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe fixture cleanup path");
            Directory.Delete(root, true);
        }
    }
}
