using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CSharp;
using Newtonsoft.Json.Linq;
using LxMusic.Bridge.Online;

static class UpdateRegression
{
    static int checks;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "lx-update-tests-" + Guid.NewGuid().ToString("N"));
    static void Check(bool value, string reason) { ++checks; if (!value) throw new Exception(reason); }
    static void Reject(Action action, string reason)
    {
        try { action(); } catch (Exception) { ++checks; return; }
        throw new Exception(reason);
    }
    static string Failure(Action action)
    {
        try { action(); } catch (Exception ex) { return ex.Message; }
        throw new Exception("failure expected");
    }
    static string Fresh() { var path = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static string Hash(byte[] data) { using (var sha = SHA256.Create()) return "sha256:" + BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }

    static byte[] Pe(string architecture)
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(128).CopyTo(bytes, 60);
        BitConverter.GetBytes(0x4550).CopyTo(bytes, 128);
        BitConverter.GetBytes((ushort)(architecture == "x64" ? 0x8664 : 0x14c)).CopyTo(bytes, 132);
        return bytes;
    }
    static byte[] Package(string architecture = "x64", string extra = null, bool missing = false, bool duplicate = false)
    {
        using (var data = new MemoryStream())
        {
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
            {
                Action<string, byte[]> add = (name, bytes) => { using (var stream = zip.CreateEntry(name).Open()) stream.Write(bytes, 0, bytes.Length); };
                add("foo_lxmusic.dll", Pe(architecture));
                if (!missing) add("foo_lx_bridge.exe", Encoding.UTF8.GetBytes("new bridge"));
                add("README.md", Encoding.UTF8.GetBytes("new readme"));
                add("licenses/fixture.txt", Encoding.UTF8.GetBytes("license"));
                if (extra != null) add(extra, new byte[] { 1 });
                if (duplicate) add("FOO_LXMUSIC.DLL", new byte[] { 1 });
            }
            return data.ToArray();
        }
    }
    static ComponentUpdate Update(byte[] bytes, string architecture = "x64") => new ComponentUpdate
    {
        Version = "v0.1.8", Url = "https://github.com/lumiliya2009/foo_lxmusic/releases/tag/v0.1.8",
        Architecture = architecture, PackageName = UpdateChecker.PackageName("v0.1.8", architecture),
        PackageUrl = "https://github.com/lumiliya2009/foo_lxmusic/releases/download/v0.1.8/" + UpdateChecker.PackageName("v0.1.8", architecture),
        Size = bytes.Length, Sha256 = Hash(bytes)
    };
    static JObject Release(byte[] bytes)
    {
        Func<string, JObject> asset = architecture =>
        {
            var update = Update(bytes, architecture);
            return new JObject { ["name"] = update.PackageName, ["state"] = "uploaded",
                ["size"] = update.Size, ["digest"] = update.Sha256, ["browser_download_url"] = update.PackageUrl };
        };
        return new JObject { ["tag_name"] = "v0.1.8", ["html_url"] = Update(bytes).Url,
            ["body"] = "- 支持了咪咕和汽水音乐的搜索和播放\n- 修复其他问题",
            ["assets"] = new JArray(asset("x86"), asset("x64")) };
    }

    static void TestMetadata()
    {
        var bytes = Package();
        var release = Release(bytes);
        var parsed = UpdateChecker.Parse(release.ToString(), "0.1.7", "x86");
        Check(parsed.Changelog.Contains("咪咕和汽水音乐") && parsed.Changelog.Contains("修复其他问题"),
            "release changelog was not retained");
        var manifest = (JObject)release.DeepClone();
        manifest.Remove("body");
        manifest["changelog"] = "静态更新清单";
        Check(UpdateChecker.Parse(manifest.ToString(), "0.1.7", "x64").Changelog == "静态更新清单",
            "static manifest changelog was ignored");
        Check(UpdateChecker.Parse(release.ToString(), "0.1.7", "x86").PackageName.EndsWith("fb1-x86.fb2k-component"), "x86 asset selection failed");
        Check(UpdateChecker.Parse(release.ToString(), "0.1.7", "x64").PackageName.EndsWith("fb2-x64.fb2k-component"), "x64 asset selection failed");
        Check(UpdateChecker.Parse(release.ToString(), "0.1.8", "x64") == null, "same version offered");
        Check(UpdateChecker.Parse(release.ToString(), "0.1.9", "x64") == null, "downgrade offered");
        Reject(() => UpdateChecker.Parse(release.ToString(), "0.1.7", "arm64"), "unsupported architecture accepted");
        foreach (var field in new[] { "digest", "size", "state", "browser_download_url", "name" })
        {
            var bad = (JObject)release.DeepClone();
            bad["assets"][1][field] = field == "size" ? (JToken)0 : "invalid";
            Reject(() => UpdateChecker.Parse(bad.ToString(), "0.1.7", "x64"), "bad asset field accepted: " + field);
        }
        var missing = (JObject)release.DeepClone(); missing["assets"] = new JArray();
        Reject(() => UpdateChecker.Parse(missing.ToString(), "0.1.7", "x64"), "missing package accepted");
        var duplicate = (JObject)release.DeepClone(); ((JArray)duplicate["assets"]).Add(duplicate["assets"][1].DeepClone());
        Reject(() => UpdateChecker.Parse(duplicate.ToString(), "0.1.7", "x64"), "ambiguous package accepted");
        var foreign = (JObject)release.DeepClone(); foreign["assets"][1]["browser_download_url"] = Update(bytes).PackageUrl.Replace("lumiliya2009", "other");
        Reject(() => UpdateChecker.Parse(foreign.ToString(), "0.1.7", "x64"), "foreign package accepted");
        var draft = (JObject)release.DeepClone(); draft["draft"] = true;
        Check(UpdateChecker.Parse(draft.ToString(), "0.1.7", "x64") == null, "draft offered");
        draft.Remove("draft"); draft["prerelease"] = true;
        Check(UpdateChecker.Parse(draft.ToString(), "0.1.7", "x64") == null, "prerelease offered");
    }

    static void TestFallbacks()
    {
        var bytes = Package();
        var current = Release(bytes).ToString();
        var newer = current.Replace("0.1.8", "0.1.10");
        foreach (var feed in new[] { "api.github.com", "raw.githubusercontent.com", "cdn.jsdelivr.net", "fastly.jsdelivr.net", "gcore.jsdelivr.net" })
        foreach (var directRoute in new[] { false, true })
        {
            var attempts = 0;
            var result = UpdateChecker.Check("0.1.7", "x64", (url, direct) => {
                Interlocked.Increment(ref attempts);
                if (new Uri(url).Host != feed || direct != directRoute) throw new IOException("offline route");
                return current;
            });
            Check(result?.Version == "v0.1.8" && attempts == 10, "fallback failed: " + feed + "/" + directRoute);
        }
        var latest = UpdateChecker.Check("0.1.7", "x86", (url, direct) => {
            if (url.Contains("api.github")) { Thread.Sleep(80); return newer; }
            return current;
        });
        Check(latest?.Version == "v0.1.10", "fast stale CDN hid newer API result");
        latest = UpdateChecker.Check("0.1.9", "x64", (url, direct) => url.Contains("raw.githubusercontent") ? newer : current);
        Check(latest?.Version == "v0.1.10", "stale no-update result hid newer manifest");
        Check(UpdateChecker.Check("0.1.10", "x64", (url, direct) => newer) == null, "latest version prompted");
        Reject(() => UpdateChecker.Check("0.1.7", "x64", (url, direct) => { throw new IOException("offline"); }), "total outage reported latest");
        Reject(() => UpdateChecker.Check("0.1.7", "x64", (url, direct) => "<html>blocked</html>"), "invalid feeds reported latest");
        latest = UpdateChecker.Check("0.1.7", "x64", (url, direct) => url.Contains("jsdelivr") ? current : "invalid");
        Check(latest?.Version == "v0.1.8", "malformed feed blocked valid fallback");

        var routes = new List<bool>();
        var downloaded = UpdateInstaller.DownloadPackage(Update(bytes), (url, direct) => {
            routes.Add(direct);
            if (!direct) throw new IOException("dead proxy");
            return bytes;
        });
        Check(downloaded.SequenceEqual(bytes) && routes.SequenceEqual(new[] { true }), "package direct mirror failed");
        routes.Clear();
        downloaded = UpdateInstaller.DownloadPackage(Update(bytes), (url, direct) => {
            routes.Add(direct);
            if (direct) throw new IOException("direct blocked");
            return bytes;
        });
        Check(downloaded.SequenceEqual(bytes) && routes.SequenceEqual(new[] { true, false }), "mirror proxy fallback failed");
        var corruptAttempts = 0;
        downloaded = UpdateInstaller.DownloadPackage(Update(bytes), (url, direct) => ++corruptAttempts == 1 ? new byte[bytes.Length] : bytes);
        Check(downloaded.SequenceEqual(bytes) && corruptAttempts == 2, "corrupt package did not retry");
        var packageUrls = UpdateChecker.PackageUrls(Update(bytes));
        Check(packageUrls.Length == 7 &&
            packageUrls[0].StartsWith("https://gcore.jsdelivr.net/gh/lumiliya2009/foo_lxmusic@v0.1.8/downloads/") &&
            packageUrls[1].StartsWith("https://ghfast.top/https://github.com/") &&
            packageUrls[2].StartsWith("https://gh-proxy.org/https://github.com/") &&
            packageUrls[3].StartsWith("https://gh-proxy.com/https://github.com/") &&
            packageUrls[4].StartsWith("https://fastly.jsdelivr.net/") && packageUrls[5].StartsWith("https://cdn.jsdelivr.net/") &&
            packageUrls[6].StartsWith("https://github.com/"), "package mirror order or repository path invalid");
        Check(Failure(() => UpdateInstaller.DownloadPackage(Update(bytes), (url, direct) => new byte[bytes.Length])).Contains("SHA256"),
            "corrupt package failure hid the checksum cause");
        Check(Failure(() => UpdateInstaller.DownloadPackage(Update(bytes), (url, direct) => { throw new IOException("offline"); })).Contains("offline"),
            "package outage hid the network cause");
    }

    static void TestPackages()
    {
        foreach (var architecture in new[] { "x86", "x64" })
        {
            var bytes = Package(architecture);
            var payload = Path.Combine(Fresh(), "payload");
            var files = UpdateInstaller.StagePackage(Update(bytes, architecture), bytes, payload);
            Check(files.Count == 4 && File.ReadAllBytes(Path.Combine(payload, "foo_lxmusic.dll")).SequenceEqual(Pe(architecture)), "valid package failed");
            Reject(() => UpdateInstaller.StagePackage(Update(bytes, architecture), bytes, payload), "existing stage overwritten");
            var corrupted = (byte[])bytes.Clone(); corrupted[corrupted.Length - 1] ^= 1;
            Reject(() => UpdateInstaller.StagePackage(Update(bytes, architecture), corrupted, Path.Combine(Fresh(), "payload")), "bad SHA accepted");
            Reject(() => UpdateInstaller.StagePackage(Update(bytes, architecture), bytes.Take(bytes.Length - 1).ToArray(), Path.Combine(Fresh(), "payload")), "truncated package accepted");
            var opposite = architecture == "x64" ? "x86" : "x64";
            Reject(() => UpdateInstaller.StagePackage(Update(bytes, opposite), bytes, Path.Combine(Fresh(), "payload")), "wrong PE architecture accepted");
        }
        foreach (var extra in new[] { "../escape.dll", "C:/escape.dll", "licenses/../../escape.txt", "licenses\\escape.txt", "profile/source.txt", "online-source.json", "evil.exe", "foo_lxmusic.dll:extra", "/escape.dll", "LICENSE/../README.md" })
        {
            var bytes = Package(extra: extra);
            Reject(() => UpdateInstaller.StagePackage(Update(bytes), bytes, Path.Combine(Fresh(), "payload")), "unsafe entry accepted: " + extra);
        }
        var missing = Package(missing: true);
        Reject(() => UpdateInstaller.StagePackage(Update(missing), missing, Path.Combine(Fresh(), "payload")), "incomplete package accepted");
        var duplicate = Package(duplicate: true);
        Reject(() => UpdateInstaller.StagePackage(Update(duplicate), duplicate, Path.Combine(Fresh(), "payload")), "case collision accepted");
    }

    static string Target(string parent)
    {
        var target = Path.Combine(parent, "foo_lxmusic"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "foo_lxmusic.dll"), "old dll");
        File.WriteAllText(Path.Combine(target, "foo_lx_bridge.exe"), "old bridge");
        File.WriteAllText(Path.Combine(target, "user-data.txt"), "keep user data");
        return target;
    }

    static void TestInstall()
    {
        var root = Fresh(); var target = Target(root); var payload = Path.Combine(root, "payload");
        var bytes = Package(); var files = UpdateInstaller.StagePackage(Update(bytes), bytes, payload);
        var backup = Path.Combine(root, "backup");
        UpdateInstaller.InstallPayload(payload, target, backup, files);
        Check(File.ReadAllBytes(Path.Combine(target, "foo_lxmusic.dll")).SequenceEqual(Pe("x64")), "DLL not replaced");
        Check(File.ReadAllText(Path.Combine(target, "foo_lx_bridge.exe")) == "new bridge", "bridge not replaced");
        Check(File.ReadAllText(Path.Combine(target, "licenses/fixture.txt")) == "license", "nested file not installed");
        Check(File.ReadAllText(Path.Combine(target, "user-data.txt")) == "keep user data", "user data changed");
        Check(File.ReadAllText(Path.Combine(backup, "foo_lxmusic.dll")) == "old dll", "backup lost");

        root = Fresh(); target = Target(root); backup = Path.Combine(root, "backup");
        using (new FileStream(Path.Combine(target, "foo_lxmusic.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => UpdateInstaller.InstallPayload(payload, target, backup, files, 0), "locked DLL was replaced");
        Check(File.ReadAllText(Path.Combine(target, "foo_lxmusic.dll")) == "old dll", "locked DLL lost");
        Check(File.ReadAllText(Path.Combine(target, "foo_lx_bridge.exe")) == "old bridge", "partial update not rolled back");
        Check(!File.Exists(Path.Combine(target, "README.md")), "new file not removed on rollback");
        Check(!Directory.GetFiles(target, "*.lx-new-*", SearchOption.AllDirectories).Any(), "temporary files leaked");
        using (new FileStream(Path.Combine(target, ".lx-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Reject(() => UpdateInstaller.InstallPayload(payload, target, Path.Combine(root, "backup2"), files), "concurrent updater accepted");
        Reject(() => UpdateInstaller.InstallPayload(payload, root, Path.Combine(root, "backup3"), files), "shared components directory accepted");
    }

    static void WaitFor(Func<bool> condition, string reason)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > 20000) throw new Exception(reason); Thread.Sleep(50); }
    }

    static void TestHelper(bool cancel, ComponentUpdate liveUpdate = null)
    {
        var root = Fresh(); var host = Path.Combine(root, "fixture-host.exe");
        const string source = @"using System; using System.IO; using System.Threading;
class Host { static void Main() {
var root = AppDomain.CurrentDomain.BaseDirectory;
if (File.Exists(Path.Combine(root, ""started""))) { File.WriteAllText(Path.Combine(root, ""restarted""), ""yes""); return; }
File.WriteAllText(Path.Combine(root, ""started""), ""yes"");
while (!File.Exists(Path.Combine(root, ""exit""))) Thread.Sleep(50);
} }";
        using (var compiler = new CSharpCodeProvider())
        {
            var parameters = new CompilerParameters { GenerateExecutable = true, OutputAssembly = host, CompilerOptions = "/target:winexe" };
            parameters.ReferencedAssemblies.Add("System.dll");
            var compilation = compiler.CompileAssemblyFromSource(parameters, source);
            Check(!compilation.Errors.HasErrors, "host fixture compilation failed");
        }
        var target = Target(root); var bytes = Package();
        using (var parent = Process.Start(new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true }))
        {
            try
            {
                WaitFor(() => File.Exists(Path.Combine(root, "started")), "host fixture did not start");
                var stage = liveUpdate == null
                    ? UpdateInstaller.PrepareDownloaded(Update(bytes), bytes, Path.Combine(root, "updates"), target, host, parent.Id)
                    : UpdateInstaller.Prepare(liveUpdate, Path.Combine(root, "updates"), target, host, parent.Id);
                Check(File.Exists(Path.Combine(stage, "ready")) && !parent.HasExited, "helper not ready while parent running");
                Check(File.ReadAllText(Path.Combine(target, "foo_lxmusic.dll")) == "old dll", "updated before parent exit");
                if (cancel)
                {
                    File.WriteAllText(Path.Combine(stage, "cancel"), "cancel");
                    WaitFor(() => File.Exists(Path.Combine(stage, "result.txt")), "helper did not cancel");
                    Check(File.ReadAllText(Path.Combine(stage, "result.txt")) == "cancelled", "cancellation result incorrect");
                    Check(File.ReadAllText(Path.Combine(target, "foo_lxmusic.dll")) == "old dll", "cancel changed installed DLL");
                    Check(!File.Exists(Path.Combine(root, "restarted")) && !parent.HasExited, "cancel interrupted host");
                }
                else
                {
                    File.WriteAllText(Path.Combine(root, "exit"), "exit");
                    WaitFor(() => File.Exists(Path.Combine(root, "restarted")), "helper did not restart host");
                    Check(File.ReadAllText(Path.Combine(stage, "result.txt")) == "success", "helper installation failed");
                    Check(File.ReadAllBytes(Path.Combine(target, "foo_lxmusic.dll")).SequenceEqual(
                        File.ReadAllBytes(Path.Combine(stage, "payload", "foo_lxmusic.dll"))), "helper did not install DLL");
                    Check(File.ReadAllText(Path.Combine(target, "user-data.txt")) == "keep user data", "helper changed user data");
                    WaitFor(() => { UpdateInstaller.CleanupStages(Path.Combine(root, "updates")); return !Directory.Exists(stage); },
                        "successful update stage was kept");
                }
            }
            finally { File.WriteAllText(Path.Combine(root, "exit"), "exit"); parent.WaitForExit(5000); }
        }
    }

    static string Stage(string updates, string name, string result)
    {
        var stage = Path.Combine(updates, name);
        Directory.CreateDirectory(Path.Combine(stage, "runner"));
        File.WriteAllText(Path.Combine(stage, "runner", "foo_lx_bridge.exe"), "runner");
        if (result != null) File.WriteAllText(Path.Combine(stage, "result.txt"), result);
        return stage;
    }

    static void TestCleanup()
    {
        var updates = Path.Combine(Fresh(), "updates");
        var done = Stage(updates, "done", "cancelled");
        var rollback = Stage(updates, "rollback", "rollback-failed: backup");
        var running = Stage(updates, "running", null);
        var abandoned = Stage(updates, "abandoned", null);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));
        UpdateInstaller.CleanupStages(updates);
        Check(!Directory.Exists(done) && !Directory.Exists(abandoned), "finished update stage was kept");
        Check(Directory.Exists(rollback) && Directory.Exists(running), "running or failed-rollback stage was deleted");
    }

    static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(Root);
            TestMetadata(); TestFallbacks(); TestPackages(); TestInstall(); TestCleanup(); TestHelper(false); TestHelper(true);
            if (args.Contains("--live") || args.Contains("--live-check-only"))
            {
                var proxy = args.FirstOrDefault(arg => arg.StartsWith("--proxy=", StringComparison.Ordinal));
                if (proxy != null) System.Net.WebRequest.DefaultWebProxy = new System.Net.WebProxy(proxy.Substring(8));
                foreach (var architecture in new[] { "x86", "x64" })
                {
                    var update = UpdateChecker.Check("0.1.6", architecture);
                    Check(update != null, "live release missing");
                    if (args.Contains("--live-check-only")) Console.WriteLine("Verified live update check: " + update.Version + " " + architecture);
                    else
                    {
                        TestHelper(false, update);
                        Console.WriteLine("Verified live download/install: " + update.Version + " " + architecture);
                    }
                }
            }
            Console.WriteLine("PASS: " + checks + " updater checks; staged helper, rollback, cancellation and restart verified");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Fixtures: " + Root); return 1; }
        finally
        {
            try
            {
                var resolved = Path.GetFullPath(Root);
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("lx-update-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
            }
            catch { }
        }
    }
}
