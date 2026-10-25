using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal sealed class SolutionEntry
    {
        public string Path { get; private set; }
        public SolutionEntry(string path) { Path = path; }
        public override string ToString() { return SolutionPaths.FileName(Path); }
    }

    internal static class SolutionPaths
    {
        public static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.Substring(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1);
        }
        public static string DirectoryName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            int split = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            if (split < 0) return string.Empty;
            return path.Substring(0, split + 1);
        }
        public static bool IsSolution(string name)
        {
            return name.EndsWith(".sol", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".solx", StringComparison.OrdinalIgnoreCase);
        }
        public static string Join(string directory, string name)
        {
            if (name != FileName(name) || name == "." || name == "..") throw new InvalidOperationException("원격 파일명 형식을 확인할 수 없습니다: " + name);
            return directory.TrimEnd('/', '\\') + (directory.Contains("\\") ? "\\" : "/") + name;
        }
        public static bool Same(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            bool windows = left.Contains("\\") || (left.Length > 1 && left[1] == ':');
            return string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    internal interface ISolutionSession
    {
        string CurrentPath { get; }
        IList<SolutionEntry> List(string currentPath);
        void Load(string path);
    }

    internal interface IVerifiedSolutionSession : ISolutionSession
    {
        string VerifiedCurrentPath { get; }
    }

    internal sealed class SolutionPathConflictException : InvalidOperationException
    {
        public SolutionPathConflictException(string message) : base(message) { }
    }

    internal sealed class RemoteSolutionSession : IVerifiedSolutionSession
    {
        private readonly string _directory;
        private readonly string _password;
        public RemoteSolutionSession(string directory, string password) { _directory = directory; _password = password ?? string.Empty; }
        public string CurrentPath
        {
            get
            {
                return ReadDevicePath();
            }
        }
        public string VerifiedCurrentPath
        {
            get { return ResolveVerifiedPath(VmSolution.Instance.SolutionPath, ReadDevicePath()); }
        }
        internal static string ResolveVerifiedPath(string cached, string device)
        {
            // A remote attachment may not populate the managed ModuleFilePath cache.
            // Use the actual SDK device response, never the ComboBox selection, as fallback.
            if (string.IsNullOrWhiteSpace(cached)) return device;
            if (!SolutionPaths.Same(cached, device))
                throw new SolutionPathConflictException("SolutionPath와 장비 응답이 일치하지 않아 변경을 중단했습니다.\r\nSolutionPath: " + cached + "\r\n장비 응답: " + device);
            return cached;
        }
        private static string ReadDevicePath()
        {
            const uint capacity = 4096;
            IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
            try
            {
                uint length = 0;
                int error = ServerSDKAPI.IMVS_GetSolutionPath(VmSolution.Instance.BaseHandle, capacity, buffer, ref length);
                if (error != 0) throw new VmException(error);
                if (length > capacity) throw new InvalidOperationException("장비가 반환한 Solution 경로 길이가 잘못되었습니다.");
                byte[] bytes = new byte[length]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }        public void Load(string path) { VmSolution.Load(path, _password); }
        public IList<SolutionEntry> List(string currentPath)
        {
            string directory = string.IsNullOrWhiteSpace(_directory) ? SolutionPaths.DirectoryName(currentPath) : _directory;
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("SolutionPath에서 원격 폴더를 확인할 수 없습니다. 실제 카메라 폴더를 SOLUTION_DIRECTORY에 지정하세요.");
            // Never treat a device drive letter as a local PC directory.
            return RemoteSolutionFiles.Read(VmSolution.Instance.BaseHandle, directory);
        }
    }

    internal static class RemoteSolutionFiles
    {
        // Exact entry points and ABI verified from VM.PlatformSDKCS.NativeMethods in the installed DLL.
        // The public FtpRemoteFileList wrapper uses these same functions, but retains temporary
        // unmanaged structure allocations. Own both buffers here and release them in finally.
        // IntPtr directory contains the same null-terminated UTF-8 bytes as the SDK UTF8Marshaler.
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_FtpFileCount", CallingConvention = CallingConvention.StdCall)]
        private static extern int FileCount(IntPtr handle, IntPtr directory, ref uint count);
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_FtpFileInfoList", CallingConvention = CallingConvention.StdCall)]
        private static extern int FileList(IntPtr handle, IntPtr directory, IntPtr list, [MarshalAs(UnmanagedType.Bool)] bool onlyName);

        internal static IList<SolutionEntry> Read(IntPtr handle, string directory)
        {
            if (handle == IntPtr.Zero) throw new InvalidOperationException("카메라 연결 핸들이 없습니다.");
            IntPtr path = IntPtr.Zero, entries = IntPtr.Zero, header = IntPtr.Zero;
            try
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(directory + "\0");
                path = Marshal.AllocHGlobal(utf8.Length); Marshal.Copy(utf8, 0, path, utf8.Length);
                uint count = 0;
                int error = FileCount(handle, path, ref count);
                if (error != 0) throw new VmException(error);
                if (count == 0) return new List<SolutionEntry>();
                if (count > 10000) throw new InvalidOperationException("원격 폴더의 파일 수가 너무 많습니다. Solution 전용 폴더를 지정하세요.");
                int stride = Marshal.SizeOf(typeof(ImvsSdkDefine.FTP_REMOTE_FILE_INFO));
                entries = Marshal.AllocHGlobal(checked((int)count * stride));
                // SDK fills fixed-size byte arrays, not separately allocated strings.
                byte[] zero = new byte[checked((int)count * stride)]; Marshal.Copy(zero, 0, entries, zero.Length);
                var info = new ImvsSdkDefine.FTP_REMOTE_FILE_LIST_INFO { FTPFileInfos = entries, FileNum = (int)count };
                header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ImvsSdkDefine.FTP_REMOTE_FILE_LIST_INFO)));
                Marshal.StructureToPtr(info, header, false);
                error = FileList(handle, path, header, false);
                if (error != 0) throw new VmException(error);
                info = (ImvsSdkDefine.FTP_REMOTE_FILE_LIST_INFO)Marshal.PtrToStructure(header, typeof(ImvsSdkDefine.FTP_REMOTE_FILE_LIST_INFO));
                if (info.FTPFileInfos != entries || info.FileNum < 0 || info.FileNum > count)
                    throw new InvalidOperationException("원격 파일 목록이 조회 중 변경되었거나 반환 형식이 다릅니다. 다시 새로고침하세요.");
                var result = new List<SolutionEntry>();
                for (int i = 0; i < info.FileNum; i++)
                {
                    var file = (ImvsSdkDefine.FTP_REMOTE_FILE_INFO)Marshal.PtrToStructure(IntPtr.Add(entries, checked(i * stride)), typeof(ImvsSdkDefine.FTP_REMOTE_FILE_INFO));
                    string name = Decode(file.FileName);
                    if (file.FilePermission != null && file.FilePermission.Length > 0 && file.FilePermission[0] == (byte)'d') continue;
                    if (SolutionPaths.IsSolution(name)) result.Add(new SolutionEntry(SolutionPaths.Join(directory, name)));
                }
                return result.GroupBy(e => e.Path, StringComparer.Ordinal).Select(g => g.First()).OrderBy(e => e.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
            }
            finally
            {
                if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
                if (entries != IntPtr.Zero) Marshal.FreeHGlobal(entries);
                if (path != IntPtr.Zero) Marshal.FreeHGlobal(path);
            }
        }
        private static string Decode(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            int count = Array.IndexOf(bytes, (byte)0); if (count < 0) count = bytes.Length;
            return new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }
    }

    internal sealed class SolutionSwitchResult
    {
        public bool Success;
        public bool ChangeAttempted;
        public bool LoadCompleted;
        public bool VerificationPending;
        public bool Unchanged;
        public bool OriginalPreserved;
        public bool RecoveryAttempted;
        public string CurrentPath;
        public Exception Error;
        public Exception RecoveryError;
    }

    internal sealed class SolutionController
    {
        private readonly ISolutionSession _session;
        public SolutionController(ISolutionSession session) { _session = session; }
        public string CurrentPath { get { return _session.CurrentPath; } }
        public IList<SolutionEntry> Refresh()
        {
            string path = null;
            try { path = _session.CurrentPath; } catch { }
            // An explicitly configured device folder can be listed even when the active path is unavailable.
            return _session.List(path);
        }
        private string VerifiedPath()
        {
            var verified = _session as IVerifiedSolutionSession;
            return verified == null ? _session.CurrentPath : verified.VerifiedCurrentPath;
        }
        public SolutionSwitchResult Switch(SolutionEntry selected)
        {
            var result = new SolutionSwitchResult();
            if (selected == null || !SolutionPaths.IsSolution(selected.Path))
            {
                result.Error = new InvalidOperationException("Solution을 선택하세요.");
                return result;
            }
            string original = null;
            try { original = VerifiedPath(); result.CurrentPath = original; }
            catch (SolutionPathConflictException ex) { result.Error = ex; try { result.CurrentPath = _session.CurrentPath; } catch { } return result; }
            catch { /* User permits Load when the current path cannot be obtained. */ }
            bool canRestore = !string.IsNullOrWhiteSpace(original);
            if (canRestore && SolutionPaths.Same(original, selected.Path))
            {
                result.Success = true; result.Unchanged = true; result.OriginalPreserved = true; return result;
            }
            try
            {
                result.ChangeAttempted = true;
                _session.Load(selected.Path);
                result.LoadCompleted = true;
                result.CurrentPath = null; // Never retain a pre-switch name as a post-switch result.
                try { result.CurrentPath = VerifiedPath(); }
                catch (Exception ex)
                {
                    if (canRestore) throw;
                    result.VerificationPending = true; result.Error = ex; return result;
                }
                if (string.IsNullOrWhiteSpace(result.CurrentPath) && !canRestore)
                {
                    result.VerificationPending = true; return result;
                }
                if (!SolutionPaths.Same(result.CurrentPath, selected.Path))
                    throw new InvalidOperationException("Load 이후 장비의 현재 경로가 선택한 경로와 일치하지 않습니다.");
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = ex;
                try { result.CurrentPath = VerifiedPath(); } catch { result.CurrentPath = null; }
                if (canRestore && !SolutionPaths.Same(result.CurrentPath, original))
                {
                    result.RecoveryAttempted = true;
                    try { _session.Load(original); result.CurrentPath = VerifiedPath(); }
                    catch (Exception recovery)
                    {
                        result.RecoveryError = recovery;
                        try { result.CurrentPath = VerifiedPath(); } catch { result.CurrentPath = null; }
                    }
                }
                result.OriginalPreserved = canRestore && SolutionPaths.Same(result.CurrentPath, original);
            }
            return result;
        }
    }
}