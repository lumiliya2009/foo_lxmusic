using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LxMusic.Bridge.Online
{
    public static class DownloadDuplicateCleaner
    {
        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav" };

        public static int Clean(string directory, IEnumerable<string> downloadedFiles,
            Action<string> progress = null, Action<string> warning = null)
        {
            var root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var created = new HashSet<string>(downloadedFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            foreach (var path in created)
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !AudioExtensions.Contains(Path.GetExtension(path)))
                    throw new InvalidOperationException("查重文件不属于本次下载目录");
            var originals = Scan(root, warning).Where(path => !created.Contains(path)).ToArray();
            var bySize = new Dictionary<long, List<string>>();
            foreach (var path in originals)
            {
                try { Add(bySize, new FileInfo(path).Length, path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { warning?.Invoke("无法检查文件：" + Path.GetFileName(path)); }
            }
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var deleted = 0;
            foreach (var candidate in created.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!SafeFile(root, candidate) || !File.Exists(candidate)) continue;
                    progress?.Invoke("正在查重：" + Path.GetFileName(candidate));
                    var size = new FileInfo(candidate).Length;
                    if (size == 0) continue;
                    var duplicate = false;
                    if (bySize.TryGetValue(size, out var matches))
                    {
                        var candidateHash = Hash(candidate);
                        foreach (var original in matches)
                        {
                            if (!SafeFile(root, original)) continue;
                            try
                            {
                                if (!hashes.TryGetValue(original, out var hash)) hashes[original] = hash = Hash(original);
                                if (hash != candidateHash) continue;
                                // Re-read both files while locked; delete the verified handle, never a path that could have been replaced.
                                if (DeleteIfEqual(original, candidate)) { duplicate = true; deleted++; break; }
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
                            { warning?.Invoke("文件被占用或无法比较，已保留：" + Path.GetFileName(candidate)); }
                        }
                    }
                    if (!duplicate) Add(bySize, size, candidate);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
                { warning?.Invoke("查重未能处理，已保留：" + Path.GetFileName(candidate)); }
            }
            return deleted;
        }

        private static void Add(Dictionary<long, List<string>> groups, long size, string path)
        {
            if (size <= 0) return;
            if (!groups.TryGetValue(size, out var list)) groups[size] = list = new List<string>();
            list.Add(path);
        }

        private static IEnumerable<string> Scan(string directory, Action<string> warning)
        {
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string[] children;
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) continue;
                    children = Directory.GetFileSystemEntries(current);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { warning?.Invoke("无法扫描部分下载目录，相关文件已保留"); continue; }
                foreach (var path in children)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else if (AudioExtensions.Contains(Path.GetExtension(path))) yield return Path.GetFullPath(path);
                }
            }
        }

        private static bool SafeFile(string root, string path)
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            for (var item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }

        private static string Hash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        private static bool DeleteIfEqual(string original, string candidate)
        {
            using (var left = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var handle = CreateFile(candidate, 0x80000000u | 0x00010000u, 1, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (var right = new FileStream(handle, FileAccess.Read))
                {
                    if (left.Length == 0 || left.Length != right.Length) return false;
                    var a = new byte[65536]; var b = new byte[65536];
                    int read;
                    while ((read = left.Read(a, 0, a.Length)) > 0)
                    {
                        var offset = 0;
                        while (offset < read)
                        {
                            var count = right.Read(b, offset, read - offset);
                            if (count == 0) return false;
                            offset += count;
                        }
                        for (var i = 0; i < read; i++) if (a[i] != b[i]) return false;
                    }
                    if (right.ReadByte() != -1) return false;
                    // FILE_DISPOSITION_INFO contains a one-byte BOOLEAN; DELETE access was requested above.
                    byte delete = 1;
                    if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return true;
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref byte info, uint size);
    }
}
