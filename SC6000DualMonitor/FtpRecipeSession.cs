using System;
using System.Collections.Generic;
using System.Linq;

namespace SC6000DualMonitor
{
    internal sealed class FtpFileRecord
    {
        public string Name, Permissions, Modified;
        public int Links;
        public long Size;
    }
    internal interface ICameraFtpApi
    {
        int Initialize();
        int SetProtocol(int protocol);
        int Activation(string ip);
        int Connect(string ip, uint port, string user, string password, ref IntPtr handle, bool encodedPassword);
        int Disconnect(IntPtr handle);
        IList<FtpFileRecord> List(IntPtr handle, string directory);
        int Delete(IntPtr handle, string path);
        int Rename(IntPtr handle, string from, string to);
    }
    internal static class CameraFtpPaths
    {
        public const string Root = "/root/vmtempfiles/ftp";
        public static string Full(string path)
        {
            path = RecipeFilePaths.Normalize(path);
            if (path != Root && !path.StartsWith(Root + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("카메라 FTP 폴더 밖의 경로입니다: " + path);
            return path;
        }
        // Vendor tool: root-relative queries; full device paths for mutations.
        public static string Relative(string path)
        { path = Full(path); return path == Root ? "/" : path.Substring(Root.Length); }
        public static string Entry(string directory, string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.Any(c => char.IsControl(c) || c == '/' || c == '\\'))
                throw new InvalidOperationException("카메라가 잘못된 파일 이름을 반환했습니다.");
            return Full(directory).TrimEnd('/') + "/" + name;
        }
    }
    internal sealed class FtpRecipeSession : IRecipeFileSession, IDisposable
    {
        private readonly Func<ICameraFtpApi> _factory;
        private readonly Func<string> _current;
        private readonly Action<string> _log;
        private readonly string _ip, _password;
        private ICameraFtpApi _api;
        private IntPtr _handle;
        private bool _disposed;
        public FtpRecipeSession(string ip, string password, Func<string> current, Func<ICameraFtpApi> factory, Action<string> log)
        { _ip = ip; _password = password; _current = current; _factory = factory; _log = log; }
        public string CurrentPath { get { if (_disposed) throw new ObjectDisposedException("FTP"); return CameraFtpPaths.Full(_current()); } }
        internal static void Check(int status, string operation)
        {
            if (status != 0) throw new InvalidOperationException(operation + " 실패 (FTPES, 오류 코드 0x" + status.ToString("X8") + ").\r\n접속 오류는 해당 카메라의 config.ini IP/PASSWORD와 연결 상태를 확인하세요.");
        }
        private void Connect()
        {
            if (_handle != IntPtr.Zero) return;
            if (string.IsNullOrWhiteSpace(_ip) || string.IsNullOrEmpty(_password))
                throw new InvalidOperationException("해당 카메라의 config.ini IP와 PASSWORD를 입력하세요.");
            if (_api == null) _api = _factory();
            Check(_api.Initialize(), "FTP 초기화");
            Check(_api.SetProtocol(2), "FTPES 설정");
            Check(_api.Activation(_ip), "카메라 활성화 상태 확인");
            // PASSWORD contains a plaintext camera password, not a cached E1 credential.
            Check(_api.Connect(_ip, 21, "vmftp", _password, ref _handle, false), "카메라 FTP 로그인");
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("FTP 로그인 후 연결 핸들을 받지 못했습니다.");
            if (_log != null) _log("FTPES connected: " + _ip + ":21 user=vmftp");
        }
        private T Run<T>(Func<T> action)
        {
            var current = CurrentPath; // Check original SDK attachment before every FTP operation.
            try { Connect(); return action(); }
            catch (Exception ex)
            {
                Disconnect(); // No retry, including an ambiguous delete/rename response.
                if (_log != null) _log("FTP operation failed: " + ex.GetType().Name);
                throw;
            }
        }
        private IList<FtpFileRecord> Entries(string directory)
        { return _api.List(_handle, CameraFtpPaths.Relative(directory)); }
        public IList<RecipeFile> List(string directory)
        {
            directory = CameraFtpPaths.Full(directory);
            return Run<IList<RecipeFile>>(() => {
                var result = new List<RecipeFile>();
                foreach (var record in Entries(directory))
                {
                    if (record.Name == "." || record.Name == "..") continue;
                    string path = CameraFtpPaths.Entry(directory, record.Name);
                    if (string.IsNullOrEmpty(record.Permissions) || record.Permissions[0] != '-' || record.Links > 1 || !RecipeFilePaths.IsRecipe(path)) continue;
                    result.Add(new RecipeFile { Path = path, Size = record.Size, Modified = record.Modified });
                }
                return result;
            });
        }
        public bool Exists(string path)
        {
            path = CameraFtpPaths.Full(path);
            return Run(() => {
                string directory = RecipeFilePaths.Directory(path);
                // Include folders/links so rename never overwrites an excluded entry.
                return Entries(directory).Where(r => r.Name != "." && r.Name != "..")
                    .Any(r => CameraFtpPaths.Entry(directory, r.Name) == path);
            });
        }
        public void Delete(string path)
        { path = CameraFtpPaths.Full(path); Run(() => { Check(_api.Delete(_handle, path), "파일 삭제"); if (_log != null) _log("FTP delete: " + path); return true; }); }
        public void Rename(string from, string to)
        { from = CameraFtpPaths.Full(from); to = CameraFtpPaths.Full(to); Run(() => { Check(_api.Rename(_handle, from, to), "이름 변경"); if (_log != null) _log("FTP rename: " + from + " -> " + to); return true; }); }
        private void Disconnect()
        {
            var handle = _handle; _handle = IntPtr.Zero;
            if (handle == IntPtr.Zero || _api == null) return;
            try { int status = _api.Disconnect(handle); if (status != 0 && _log != null) _log("FTP disconnect error=0x" + status.ToString("X8")); }
            catch { if (_log != null) _log("FTP disconnect failed"); }
        }
        public void Dispose() { if (_disposed) return; _disposed = true; Disconnect(); }
    }
}
