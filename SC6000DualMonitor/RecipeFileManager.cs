using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SC6000DualMonitor
{
    internal sealed class RecipeFile
    {
        public string Path, Modified;
        public long Size;
        public string Name { get { return RecipeFilePaths.Name(Path); } }
    }
    internal sealed class RecipeFileSnapshot
    {
        public string Current, Directory;
        public RecipeFile[] Files;
    }
    internal interface IRecipeFileSession
    {
        string CurrentPath { get; }
        IList<RecipeFile> List(string directory);
        bool Exists(string path);
        void Delete(string path);
        void Rename(string from, string to);
    }
    internal static class RecipeFilePaths
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
                throw new InvalidOperationException("카메라의 레시피 경로를 확인할 수 없습니다. 다시 연결하세요.");
            path = path.Replace('\\', '/');
            bool drive = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/';
            if ((!path.StartsWith("/", StringComparison.Ordinal) && !drive) || path.StartsWith("//", StringComparison.Ordinal))
                throw new InvalidOperationException("카메라 내부의 전체 레시피 경로가 필요합니다.");
            string[] parts = path.Split('/');
            if (parts.Any(p => p == "." || p == "..")) throw new InvalidOperationException("상위 폴더 이동이 포함된 경로는 사용할 수 없습니다.");
            string normalized = (drive ? "" : "/") + string.Join("/", parts.Where(p => p.Length > 0));
            return drive && normalized.Length == 2 ? normalized + "/" : normalized;
        }
        public static bool Same(string a, string b)
        {
            a = Normalize(a); b = Normalize(b);
            return string.Equals(a, b, a.Length > 1 && a[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        public static string Name(string path) { return path.Substring(path.LastIndexOf('/') + 1); }
        public static string Directory(string path)
        {
            string directory = path.Substring(0, path.LastIndexOf('/') + 1);
            return directory == "/" || (directory.Length == 3 && directory[1] == ':') ? directory : directory.TrimEnd('/');
        }
        public static bool IsRecipe(string path)
        { return path.EndsWith(".solx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sol", StringComparison.OrdinalIgnoreCase); }
        public static string Renamed(string path, string stem)
        {
            stem = (stem ?? "").Trim();
            if (stem.Length == 0 || stem == "." || stem == ".." || stem.EndsWith(".", StringComparison.Ordinal) ||
                stem.Any(c => char.IsControl(c) || "/\\:*?\"<>|".IndexOf(c) >= 0))
                throw new InvalidOperationException("새 이름을 입력하세요. / \\ : * ? \" < > | 문자는 사용할 수 없습니다.");
            string extension = path.Substring(path.LastIndexOf('.'));
            if (IsRecipe(stem)) stem = stem.Substring(0, stem.LastIndexOf('.')); // Accept a pasted full recipe name; retain original extension.
            if (stem.Length == 0 || stem.EndsWith(".", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(stem + extension) > 240)
                throw new InvalidOperationException("파일 이름이 비어 있거나 너무 깁니다. 더 짧은 이름을 입력하세요.");
            return Directory(path).TrimEnd('/') + "/" + stem + extension;
        }
    }
    // All destructive operations validate a fresh list and device path after the operator confirms.
    internal sealed class RecipeFileManager
    {
        private readonly IRecipeFileSession _session;
        private readonly string _configuredDirectory;
        public RecipeFileSnapshot Snapshot { get; private set; }
        public RecipeFileManager(IRecipeFileSession session, string directory)
        { _session = session; _configuredDirectory = directory; }
        private string Current()
        {
            string path = RecipeFilePaths.Normalize(_session.CurrentPath);
            if (!RecipeFilePaths.IsRecipe(path)) throw new InvalidOperationException("현재 사용 중인 레시피를 확인할 수 없어 파일 변경을 막았습니다.");
            return path;
        }
        private RecipeFile[] List(string directory)
        {
            var files = _session.List(directory).ToArray();
            foreach (var file in files)
            {
                file.Path = RecipeFilePaths.Normalize(file.Path);
                if (!RecipeFilePaths.IsRecipe(file.Path) || !RecipeFilePaths.Same(RecipeFilePaths.Directory(file.Path), directory))
                    throw new InvalidOperationException("레시피 폴더 밖의 파일이 반환되어 작업을 중단했습니다.");
            }
            if (files.Select(f => f.Path).Distinct(directory.Length > 1 && directory[1] == ':' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != files.Length)
                throw new InvalidOperationException("파일 목록에 중복 경로가 있습니다. 새로고침하세요.");
            return files;
        }
        public RecipeFileSnapshot Refresh()
        {
            Snapshot = null;
            string current = Current();
            string directory = RecipeFilePaths.Normalize(string.IsNullOrWhiteSpace(_configuredDirectory) ? RecipeFilePaths.Directory(current) : _configuredDirectory);
            var files = List(directory);
            if (!RecipeFilePaths.Same(current, Current())) throw new InvalidOperationException("조회 중 사용 레시피가 바뀌었습니다. 새로고침하세요.");
            Snapshot = new RecipeFileSnapshot { Current = current, Directory = directory,
                Files = files.OrderByDescending(f => RecipeFilePaths.Same(f.Path, current)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray() };
            return Snapshot;
        }
        private void CheckCurrent(RecipeFileSnapshot snapshot, string target)
        {
            string current = Current();
            if (!RecipeFilePaths.Same(current, snapshot.Current)) throw new InvalidOperationException("사용 레시피가 바뀌었습니다. 목록을 새로고침한 뒤 다시 선택하세요.");
            if (RecipeFilePaths.Same(target, current)) throw new InvalidOperationException("현재 사용 중인 레시피는 삭제하거나 이름을 바꿀 수 없습니다.");
        }
        private RecipeFileSnapshot Validate(string target)
        {
            var snapshot = Snapshot;
            if (snapshot == null) throw new InvalidOperationException("목록을 새로고침한 뒤 파일을 선택하세요.");
            target = RecipeFilePaths.Normalize(target);
            var selected = snapshot.Files.SingleOrDefault(f => RecipeFilePaths.Same(f.Path, target));
            if (selected == null) throw new InvalidOperationException("목록에서 레시피를 선택하세요.");
            CheckCurrent(snapshot, target);
            var fresh = List(snapshot.Directory).SingleOrDefault(f => RecipeFilePaths.Same(f.Path, target));
            if (fresh == null || fresh.Size != selected.Size || fresh.Modified != selected.Modified)
                throw new InvalidOperationException("선택한 파일이 변경되거나 없어졌습니다. 목록을 새로고침하세요.");
            return snapshot;
        }
        public void Delete(string path)
        {
            path = RecipeFilePaths.Normalize(path);
            var snapshot = Validate(path);
            CheckCurrent(snapshot, path);
            Snapshot = null; // Success or ambiguous network failure both invalidate the old list. Never retry automatically.
            _session.Delete(path);
            if (_session.Exists(path)) throw new InvalidOperationException("삭제 요청 후에도 파일이 남아 있습니다. 새로고침하여 확인하세요.");
        }
        public string Rename(string path, string stem)
        {
            path = RecipeFilePaths.Normalize(path);
            string target = RecipeFilePaths.Renamed(path, stem);
            if (RecipeFilePaths.Same(path, target)) throw new InvalidOperationException("기존 이름과 다른 이름을 입력하세요.");
            var snapshot = Validate(path);
            if (_session.Exists(target)) throw new InvalidOperationException("같은 이름의 파일 또는 폴더가 있습니다. 다른 이름을 입력하세요.");
            CheckCurrent(snapshot, path);
            Snapshot = null;
            _session.Rename(path, target);
            if (_session.Exists(path) || !_session.Exists(target)) throw new InvalidOperationException("이름 변경 결과를 확인하지 못했습니다. 새로고침하여 확인하세요.");
            return target;
        }
    }
}
