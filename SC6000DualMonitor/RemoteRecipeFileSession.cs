using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal sealed class RemoteRecipeFileSession : IRecipeFileSession
    {
        // Same entry points / StdCall ABI as the installed SDK NativeMethods (internal class).
        // Explicit UTF-8 buffers match SDK UTF8Marshaler and avoid ANSI corruption of Korean names.
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_FtpCheckFileName", CallingConvention = CallingConvention.StdCall)]
        private static extern int FileMissing(IntPtr handle, IntPtr path, [MarshalAs(UnmanagedType.Bool)] ref bool missing);
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_FtpRemoveFile", CallingConvention = CallingConvention.StdCall)]
        private static extern int RemoveFile(IntPtr handle, IntPtr path);
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_FtpRename", CallingConvention = CallingConvention.StdCall)]
        private static extern int RenameFile(IntPtr handle, IntPtr from, IntPtr to);
        private static int WithPath(string path, Func<IntPtr, int> action)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(path + "\0");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); return action(pointer); }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        private readonly VmSolution _solution;
        private readonly IntPtr _handle;
        private readonly string _camera;
        private readonly RemoteSolutionSession _paths = new RemoteSolutionSession(null, null);
        public RemoteRecipeFileSession(string camera)
        { _camera = camera; _solution = VmSolution.Instance; _handle = _solution.BaseHandle; EnsureConnection(); }
        private void EnsureConnection()
        {
            if (_handle == IntPtr.Zero || !ReferenceEquals(VmSolution.Instance, _solution) || _solution.IsDispose || _solution.BaseHandle != _handle)
                throw new InvalidOperationException("카메라 연결이 변경되었습니다. 파일 관리 창을 닫고 다시 여세요.");
        }
        public string CurrentPath { get { EnsureConnection(); return _paths.VerifiedCurrentPath; } }
        public IList<RecipeFile> List(string directory)
        { EnsureConnection(); return RemoteSolutionFiles.ReadFiles(_handle, directory, true); }
        private void Check(int error, string operation, string path)
        {
            CameraLog.Write(_camera, "RecipeFile", operation + " " + path + " result=0x" + error.ToString("X8"));
            if (error != 0) throw new InvalidOperationException(operation + " 요청을 완료하지 못했습니다. 카메라 연결과 파일 접근 권한을 확인하세요.\r\n오류 코드: 0x" + error.ToString("X8"));
        }
        public bool Exists(string path)
        {
            EnsureConnection();
            // The SDK bool wrapper discards errors. Check native status before trusting bNoExist.
            bool missing = false;
            Check(WithPath(path, p => FileMissing(_handle, p, ref missing)), "파일 확인", path);
            return !missing;
        }
        public void Delete(string path)
        { EnsureConnection(); Check(WithPath(path, p => RemoveFile(_handle, p)), "파일 삭제", path); }
        public void Rename(string from, string to)
        { EnsureConnection(); Check(WithPath(from, f => WithPath(to, t => RenameFile(_handle, f, t))), "이름 변경", from + " -> " + to); }
    }
}
