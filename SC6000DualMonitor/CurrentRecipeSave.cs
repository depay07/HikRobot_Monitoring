using System;

namespace SC6000DualMonitor
{
    // A remote attachment does not necessarily populate VmSolution.SolutionPath.
    // Always target the device path explicitly; never fall back to a Save As dialog.
    internal sealed class CurrentRecipeSave
    {
        private readonly Func<string> _readPath;
        private readonly Func<string, string> _save;
        private string _target;
        public CurrentRecipeSave(Func<string> readPath, Func<string, string> save)
        { _readPath = readPath; _save = save; }
        public void Begin()
        {
            _target = null;
            string path = _readPath();
            if (string.IsNullOrWhiteSpace(path) ||
                !(path.EndsWith(".sol", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".solx", StringComparison.OrdinalIgnoreCase)) ||
                !(path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                    (path.Length > 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))))
                throw new InvalidOperationException("카메라의 현재 레시피 전체 경로를 확인할 수 없어 저장하지 않았습니다. VisionMaster에서 레시피를 저장한 뒤 다시 연결하세요.");
            _target = path;
        }
        public void Save()
        {
            if (_target == null || !string.Equals(_target, _readPath(), StringComparison.Ordinal))
                throw new InvalidOperationException("현재 레시피가 변경되어 저장을 중단했습니다. Set Up을 닫고 다시 여세요.");
            string saved = _save(_target);
            if (!string.Equals(_target, saved, StringComparison.Ordinal))
                throw new InvalidOperationException("현재 레시피에 저장되었는지 확인할 수 없습니다.");
        }
    }
}
