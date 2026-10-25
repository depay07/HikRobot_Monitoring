using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SC6000DualMonitor
{
    internal static class FtpOriginals
    {
        private static readonly string[] Extensions = { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };
        // Walk only the documented year/month/day/NG tree, never junctions or unrelated folders.
        public static IList<NgRecord> LoadRecent(string root, string camera, int limit)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("config.ini의 RAW_IMAGE_ROOT를 설정하세요.");
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("원본 폴더 없음: " + root);
            var folders = new List<string>();
            foreach (var year in Children(root))
            foreach (var month in Children(year))
            foreach (var day in Children(month))
            {
                DateTime date;
                if (!DateTime.TryParseExact(Path.GetFileName(year) + "-" + Path.GetFileName(month) + "-" + Path.GetFileName(day),
                    new[] { "yyyy-M-d", "yyyy-MM-dd", "yyyy-M-dd", "yyyy-MM-d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                string ng = Path.Combine(day, "NG");
                if (Directory.Exists(ng) && (File.GetAttributes(ng) & FileAttributes.ReparsePoint) == 0) folders.Add(ng);
            }
            // LastWriteTime is the FTP file's saved timestamp, not its name or directory date.
            var newest = new List<FileInfo>();
            foreach (string folder in folders)
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length == 0 || file.LastWriteTimeUtc > DateTime.UtcNow.AddSeconds(-2)) continue;
                newest.Add(file);
                if (newest.Count > limit * 2) newest = newest.OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
            }
            return newest.OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Take(limit)
                .Select(f => new NgRecord { ImagePath = f.FullName, Camera = camera, TimeUtc = f.LastWriteTimeUtc,
                    ViewName = "FTP 원본 · " + f.Name, IsOriginal = true, FileLength = f.Length }).ToList();
        }
        private static IEnumerable<string> Children(string path)
        {
            return Directory.EnumerateDirectories(path).Where(p =>
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0 && Path.GetFileName(p).All(char.IsDigit));
        }
        public static byte[] ReadStable(NgRecord record)
        {
            if (!record.IsOriginal) throw new InvalidOperationException("화면 캡처는 재검사에 사용할 수 없습니다.");
            var info = new FileInfo(record.ImagePath);
            if (info.Length != record.FileLength || info.LastWriteTimeUtc != record.TimeUtc)
                throw new IOException("FTP 파일이 변경되었습니다. 목록을 새로고침한 후 다시 선택하세요.");
            if (info.Length > 128 * 1024 * 1024) throw new IOException("원본 이미지가 128MB 제한을 초과했습니다.");
            using (var file = new FileStream(record.ImagePath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var memory = new MemoryStream()) { file.CopyTo(memory); return memory.ToArray(); }
        }
    }
}
