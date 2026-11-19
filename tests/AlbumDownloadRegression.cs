using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TzPlayer.Win.Online;

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
            TestTransfers();
            if (args.Contains("--live")) TestLiveAlbum();
            Console.WriteLine(checks + " album/cover/download regression checks passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
