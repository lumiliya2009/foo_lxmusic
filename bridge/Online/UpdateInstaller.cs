using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace LxMusic.Bridge.Online
{
    // Runs from a private staging directory after foobar2000 releases its DLLs.
    public static class UpdateInstaller
    {
        private sealed class RollbackException : IOException
        {
            public RollbackException(string message, Exception inner) : base(message, inner) { }
        }

        private sealed class InstallPlan
        {
            public ComponentUpdate Update;
            public string Target, Host;
            public string OriginalHash;
            public int ParentId;
            public long ParentStarted;
        }

        public static string Prepare(ComponentUpdate update, string updatesDirectory,
            string componentDirectory, string host, int parentId)
        {
            UpdateChecker.ValidatePackage(update);
            ValidateTarget(componentDirectory);
            var bytes = DownloadPackage(update);
            return PrepareDownloaded(update, bytes, updatesDirectory, componentDirectory, host, parentId);
        }

        public static byte[] DownloadPackage(ComponentUpdate update, Func<string, bool, byte[]> fetch = null)
        {
            UpdateChecker.ValidatePackage(update);
            foreach (var url in UpdateChecker.PackageUrls(update))
            {
                var mirror = new Uri(url).Host.EndsWith(".jsdelivr.net", StringComparison.OrdinalIgnoreCase) ||
                    new Uri(url).Host.Equals("jsdelivr.net", StringComparison.OrdinalIgnoreCase);
                foreach (var direct in mirror ? new[] { true, false } : new[] { false, true })
                {
                    try
                    {
                        byte[] bytes;
                        if (fetch != null) bytes = fetch(url, direct);
                        else
                        {
                            var response = OnlineHttp.Execute(url, timeoutMs: 20000,
                                maxBytes: UpdateChecker.MaxPackageBytes, useSystemProxy: true, forceDirect: direct);
                            if (!response.Ok) continue;
                            bytes = response.BodyBytes;
                        }
                        if (bytes != null && bytes.LongLength == update.Size && string.Equals(
                            "sha256:" + Hash(bytes), update.Sha256, StringComparison.OrdinalIgnoreCase)) return bytes;
                    }
                    catch { }
                }
            }
            throw new IOException("下载安装包或校验失败，请检查网络后重试，原版本未改动");
        }

        public static string PrepareDownloaded(ComponentUpdate update, byte[] bytes, string updatesDirectory,
            string componentDirectory, string host, int parentId)
        {
            UpdateChecker.ValidatePackage(update);
            componentDirectory = Path.GetFullPath(componentDirectory);
            host = Path.GetFullPath(host);
            ValidateTarget(componentDirectory);
            using (var parent = Process.GetProcessById(parentId))
            {
                if (!string.Equals(Path.GetFullPath(parent.MainModule.FileName), host, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("播放器进程与更新目标不一致");
                // Fail before asking foobar2000 to exit if its component folder is unwritable.
                var probe = Path.Combine(componentDirectory, ".lx-update-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }

                var stage = Path.Combine(Path.GetFullPath(updatesDirectory), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                StagePackage(update, bytes, Path.Combine(stage, "payload"));
                var plan = new InstallPlan
                {
                    Update = update, Target = componentDirectory, Host = host,
                    ParentId = parentId, ParentStarted = parent.StartTime.ToUniversalTime().Ticks,
                    OriginalHash = Hash(File.ReadAllBytes(Path.Combine(componentDirectory, "foo_lxmusic.dll")))
                };
                var package = Path.Combine(stage, update.PackageName);
                File.WriteAllBytes(package, bytes);
                File.WriteAllText(Path.Combine(stage, "plan.json"), JsonConvert.SerializeObject(plan), new UTF8Encoding(false));
                var runner = Path.Combine(stage, "runner");
                Directory.CreateDirectory(runner);
                var current = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                foreach (var file in Directory.GetFiles(current))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("foo_lx_bridge.exe", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("foo_lx_bridge.exe.config", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        File.Copy(file, Path.Combine(runner, name));
                }
                var start = new ProcessStartInfo(Path.Combine(runner, "foo_lx_bridge.exe"), "apply-update")
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runner
                };
                using (var helper = Process.Start(start))
                {
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(stage, "ready")))
                    {
                        if (helper.HasExited) throw new IOException("更新助手未能启动，原版本未改动");
                        if (timer.ElapsedMilliseconds > 15000)
                        {
                            File.WriteAllText(Path.Combine(stage, "cancel"), "cancel");
                            throw new IOException("更新助手启动超时，原版本未改动");
                        }
                        Thread.Sleep(50);
                    }
                }
                return stage;
            }
        }

        public static List<string> StagePackage(ComponentUpdate update, byte[] bytes, string payload)
        {
            UpdateChecker.ValidatePackage(update);
            if (bytes == null || bytes.LongLength != update.Size || !string.Equals(
                "sha256:" + Hash(bytes), update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包 SHA256 校验失败，原版本未改动");
            if (Directory.Exists(payload)) throw new IOException("更新暂存目录已存在");
            using (var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                if (archive.Entries.Count > 128) throw new InvalidDataException("安装包文件过多");
                foreach (var entry in archive.Entries)
                {
                    ValidateName(entry.FullName);
                    if (!names.Add(entry.FullName) || entry.Length < 0 || entry.Length > 128L * 1024 * 1024 - total)
                        throw new InvalidDataException("安装包包含重复文件或大小异常");
                    total += entry.Length;
                }
                if (archive.GetEntry("foo_lxmusic.dll") == null || archive.GetEntry("foo_lx_bridge.exe") == null)
                    throw new InvalidDataException("安装包缺少必要的组件文件");
                using (var entry = archive.GetEntry("foo_lxmusic.dll").Open()) ValidateArchitecture(entry, update.Architecture);
                Directory.CreateDirectory(payload);
                foreach (var entry in archive.Entries)
                {
                    var target = SafeFile(payload, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[32768];
                        long copied = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            copied += read;
                            if (copied > entry.Length) throw new InvalidDataException("安装包解压大小异常");
                            output.Write(buffer, 0, read);
                        }
                        if (copied != entry.Length) throw new InvalidDataException("安装包文件不完整");
                    }
                }
                return names.OrderBy(name => name, StringComparer.Ordinal).ToList();
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void ValidateArchitecture(Stream stream, string architecture)
        {
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[4096];
                int read;
                while (buffer.Length < bytes.Length && (read = stream.Read(bytes, 0, bytes.Length - (int)buffer.Length)) > 0)
                    buffer.Write(bytes, 0, read);
                bytes = buffer.ToArray();
                if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("组件 DLL 格式无效");
                var offset = BitConverter.ToInt32(bytes, 60);
                if (offset < 64 || offset > bytes.Length - 6 || BitConverter.ToUInt32(bytes, offset) != 0x4550 ||
                    BitConverter.ToUInt16(bytes, offset + 4) != (architecture == "x64" ? 0x8664 : 0x014c))
                    throw new InvalidDataException("安装包架构与当前播放器不一致");
            }
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 180 || name.Contains("\\") || name.Contains(":"))
                throw new InvalidDataException("安装包包含无效路径");
            var parts = name.Split('/');
            if (parts.Length > 2 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == ".." ||
                part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("安装包包含无效路径");
            if (parts.Length == 2)
            {
                if (parts[0] != "licenses" || !parts[1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包包含非组件目录");
            }
            else if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                name != "foo_lx_bridge.exe" && name != "foo_lx_bridge.exe.config" &&
                name != "README.md" && name != "LICENSE" && name != "THIRD_PARTY_NOTICES.md" && name != "BRIDGE-LICENSE.txt")
                throw new InvalidDataException("安装包包含非组件文件");
        }

        private static string SafeFile(string root, string name)
        {
            ValidateName(name);
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径超出组件目录");
            for (var item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            {
                if ((File.Exists(item) || Directory.Exists(item)) &&
                    (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新目录不能包含链接或重解析点");
            }
            return path;
        }

        private static void ValidateTarget(string directory)
        {
            if (!string.Equals(Path.GetFileName(directory.TrimEnd('\\', '/')), "foo_lxmusic", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(SafeFile(directory, "foo_lxmusic.dll")))
                throw new InvalidOperationException("请先通过 foobar2000 的组件安装功能安装完整组件包，再使用自动更新");
        }

        // Back up every destination before the first replacement. Only package-owned files are touched.
        public static void InstallPayload(string payload, string target, string backup, IList<string> files, int busyTimeoutMs = 10000)
        {
            ValidateTarget(target);
            if (files == null || !files.Contains("foo_lxmusic.dll") || !files.Contains("foo_lx_bridge.exe") ||
                files.Count != files.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new InvalidDataException("更新文件清单无效");
            using (new FileStream(Path.Combine(target, ".lx-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in files)
                {
                    if (!File.Exists(SafeFile(payload, name))) throw new FileNotFoundException("更新文件缺失", name);
                    var destination = SafeFile(target, name);
                    if (File.Exists(destination))
                    {
                        var saved = SafeFile(backup, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved));
                        File.Copy(destination, saved, false);
                        existing.Add(name);
                    }
                }
                var installed = new List<string>();
                try
                {
                    foreach (var name in files)
                    {
                        ReplaceFile(SafeFile(payload, name), SafeFile(target, name), existing.Contains(name), busyTimeoutMs);
                        installed.Add(name);
                    }
                }
                catch (Exception failure)
                {
                    var rollbackErrors = new List<Exception>();
                    foreach (var name in Enumerable.Reverse(installed))
                    {
                        try
                        {
                            if (existing.Contains(name)) ReplaceFile(SafeFile(backup, name), SafeFile(target, name), true, busyTimeoutMs);
                            else File.Delete(SafeFile(target, name));
                        }
                        catch (Exception ex) { rollbackErrors.Add(ex); }
                    }
                    if (rollbackErrors.Count > 0)
                        throw new RollbackException("更新失败，部分文件未能恢复。原文件备份：" + backup, failure);
                    throw new IOException("更新未完成，已恢复原版本：" + failure.Message, failure);
                }
            }
        }

        private static void ReplaceFile(string source, string destination, bool exists, int busyTimeoutMs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var temporary = destination + ".lx-new-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                var timer = Stopwatch.StartNew();
                for (;;)
                {
                    try
                    {
                        if (exists) File.Replace(temporary, destination, null);
                        else File.Move(temporary, destination);
                        break;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) &&
                        timer.ElapsedMilliseconds < busyTimeoutMs && File.Exists(temporary)) { Thread.Sleep(100); }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static int RunHelper()
        {
            var runner = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (Path.GetFileName(runner) != "runner") return 1;
            var stage = Path.GetDirectoryName(runner);
            InstallPlan plan = null;
            bool exited = false;
            bool installed = false;
            bool ready = false;
            try
            {
                plan = JsonConvert.DeserializeObject<InstallPlan>(File.ReadAllText(Path.Combine(stage, "plan.json")));
                UpdateChecker.ValidatePackage(plan?.Update);
                ValidateTarget(plan.Target);
                using (var parent = Process.GetProcessById(plan.ParentId))
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted ||
                        !string.Equals(parent.MainModule.FileName, plan.Host, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("播放器进程已改变，本次更新取消");
                    File.WriteAllText(Path.Combine(stage, "ready"), "ready");
                    ready = true;
                    var timer = Stopwatch.StartNew();
                    while (!parent.WaitForExit(200))
                    {
                        if (File.Exists(Path.Combine(stage, "cancel")))
                        {
                            File.WriteAllText(Path.Combine(stage, "result.txt"), "cancelled");
                            return 1;
                        }
                        if (timer.ElapsedMilliseconds > 120000)
                            throw new IOException("播放器尚未退出，本次更新已取消，请稍后重试");
                    }
                    exited = true;
                }
                if (File.Exists(Path.Combine(stage, "cancel")))
                {
                    File.WriteAllText(Path.Combine(stage, "result.txt"), "cancelled");
                    return 1;
                }
                // Re-check the saved download immediately before replacing any installed file.
                var verified = Path.Combine(stage, "verified");
                var files = StagePackage(plan.Update, File.ReadAllBytes(Path.Combine(stage, plan.Update.PackageName)), verified);
                if (Hash(File.ReadAllBytes(Path.Combine(plan.Target, "foo_lxmusic.dll"))) != plan.OriginalHash)
                    throw new IOException("组件文件在下载后发生改变，本次更新已取消");
                InstallPayload(verified, plan.Target, Path.Combine(stage, "backup"), files);
                installed = true;
                File.WriteAllText(Path.Combine(stage, "result.txt"), "success");
                Restart(plan.Host);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(stage, "result.txt"), ex.Message); } catch { }
                if (ready && plan != null)
                {
                    MessageBox.Show(installed ? "更新已安装，请手动启动 foobar2000。\n" + ex.Message : ex.Message,
                        "LX 在线音乐更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (exited && !(ex is RollbackException) && !installed) { try { Restart(plan.Host); } catch { } }
                }
                return 1;
            }
        }

        private static void Restart(string host)
        {
            Process.Start(new ProcessStartInfo(host) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(host) });
        }
    }
}
