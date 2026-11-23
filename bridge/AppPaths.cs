using System;
using System.IO;

namespace LxMusic.Bridge
{
    public static class AppPaths
    {
        private static string _baseDirectory;

        public static void Initialize(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("数据目录为空", nameof(directory));
            _baseDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(_baseDirectory);
        }

        internal static string File(string name)
        {
            if (string.IsNullOrWhiteSpace(_baseDirectory))
                throw new InvalidOperationException("尚未初始化数据目录");
            return Path.Combine(_baseDirectory, name);
        }
    }
}
