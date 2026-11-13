using System;
using System.Collections.Generic;
using VM.Core;

namespace SC6000DualMonitor
{
    internal sealed class RemoteRecipeFileSession : IRecipeFileSession, IDisposable
    {
        private readonly VmSolution _solution;
        private readonly IntPtr _handle;
        private readonly FtpRecipeSession _ftp;
        private readonly RemoteSolutionSession _paths = new RemoteSolutionSession(null, null);
        public RemoteRecipeFileSession(string camera, string password)
        {
            _solution = VmSolution.Instance; _handle = _solution.BaseHandle;
            _ftp = new FtpRecipeSession(camera, password, ReadCurrent,
                () => NativeCameraFtp.Open(typeof(VmSolution).Assembly.Location),
                message => CameraLog.Write(camera, "RecipeFileFTP", message));
        }
        private string ReadCurrent()
        {
            if (_handle == IntPtr.Zero || !ReferenceEquals(VmSolution.Instance, _solution) || _solution.IsDispose || _solution.BaseHandle != _handle)
                throw new InvalidOperationException("카메라 연결이 변경되었습니다. 파일 관리 창을 닫고 다시 여세요.");
            return _paths.VerifiedCurrentPath;
        }
        public string CurrentPath { get { return _ftp.CurrentPath; } }
        public IList<RecipeFile> List(string directory) { return _ftp.List(directory); }
        public bool Exists(string path) { return _ftp.Exists(path); }
        public void Delete(string path) { _ftp.Delete(path); }
        public void Rename(string from, string to) { _ftp.Rename(from, to); }
        public void Dispose() { _ftp.Dispose(); }
    }
}
