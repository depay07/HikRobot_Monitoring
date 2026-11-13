using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SC6000DualMonitor
{
    // Bind the installed FileTranferTool SDK; do not replace the viewer's DLL search directory.
    internal sealed class NativeCameraFtp : ICameraFtpApi
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitCall(ref uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProtocolCall(int protocol);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] private delegate int ActivationCall(string ip);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] private delegate int ConnectCall(string ip, uint port, string user, string password, ref IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool encoded);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DisconnectCall(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CountCall(IntPtr handle, IntPtr path, ref int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ListCall(IntPtr handle, IntPtr path, IntPtr header, [MarshalAs(UnmanagedType.Bool)] bool namesOnly);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeleteCall(IntPtr handle, IntPtr path);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RenameCall(IntPtr handle, IntPtr from, IntPtr to);
        [StructLayout(LayoutKind.Sequential)] internal struct FileRecord
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] FilePermission;
            public int cFileHardLink;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] FileUser;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] FileGroup;
            public int FileSize;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] FileTime;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] FileName;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct FileList { public IntPtr FTPFileInfos; public int FileNum; }
        private readonly InitCall _init;
        private readonly ProtocolCall _protocol;
        private readonly ActivationCall _activation;
        private readonly ConnectCall _connect;
        private readonly DisconnectCall _disconnect;
        private readonly CountCall _count;
        private readonly ListCall _list;
        private readonly DeleteCall _delete;
        private readonly RenameCall _rename;
        private static readonly object Gate = new object();
        private static NativeCameraFtp _instance;
        private bool _initialized;
        public static NativeCameraFtp Open(string sdkAssemblyPath)
        { lock (Gate) { return _instance ?? (_instance = new NativeCameraFtp(FindLibrary(sdkAssemblyPath))); } }
        internal static string FindLibrary(string sdkAssemblyPath)
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(sdkAssemblyPath));
            for (int i = 0; directory != null && i < 6; i++, directory = directory.Parent)
            {
                foreach (var suffix in LibrarySuffixes())
                { var candidate = Path.Combine(directory.FullName, suffix); if (File.Exists(candidate)) return candidate; }
            }
            var matches = new List<string>();
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var install in Directory.GetDirectories(root, "VisionMaster*"))
                { var candidate = LibrarySuffixes().Select(s => Path.Combine(install, s)).FirstOrDefault(File.Exists); if (candidate != null) matches.Add(candidate); }
            }
            if (matches.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1) return matches[0];
            throw new InvalidOperationException("현재 SDK와 맞는 VisionMaster FTP 라이브러리를 찾지 못했습니다. VisionMaster 설치/복구 후 다시 실행하세요.");
        }
        private static string[] LibrarySuffixes()
        {
            // FileTranferTool itself is x86. The x64 viewer must use the installed x64 counterpart.
            if (IntPtr.Size == 8) return new[] { @"PublicFile\x64\FTPClientSDK.dll", @"Applications\PublicFile\x64\FTPClientSDK.dll" };
            return new[] { @"Tools\FileTranferTool\FTPClientSDK.dll", @"Applications\Tools\FileTranferTool\FTPClientSDK.dll", @"PublicFile\x86\FTPClientSDK.dll", @"Applications\PublicFile\x86\FTPClientSDK.dll" };
        }
        private NativeCameraFtp(string path)
        {
            IntPtr library = LoadLibraryEx(Path.GetFullPath(path), IntPtr.Zero, 8); // Dependencies beside this absolute DLL path.
            if (library == IntPtr.Zero) throw new InvalidOperationException("VisionMaster FTP 라이브러리를 불러오지 못했습니다: " + path + "\r\n" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            _init = Bind<InitCall>(library, "FTP_Init"); _protocol = Bind<ProtocolCall>(library, "FTP_SetProtocol");
            _activation = Bind<ActivationCall>(library, "FTP_CheckDeviceActivationStatus"); _connect = Bind<ConnectCall>(library, "FTP_ConnectTCPIP");
            _disconnect = Bind<DisconnectCall>(library, "FTP_Disconnect"); _count = Bind<CountCall>(library, "FTP_RemoteFileCount");
            _list = Bind<ListCall>(library, "FTP_RemoteFileList"); _delete = Bind<DeleteCall>(library, "FTP_RemoveFile"); _rename = Bind<RenameCall>(library, "FTP_Rename");
            // Keep loaded for process lifetime. Never FTP_DeInit a shared SDK in a running viewer.
        }
        private static T Bind<T>(IntPtr library, string name) where T : class
        {
            var pointer = GetProcAddress(library, name);
            // The x86 SDK exports stdcall-decorated names; every argument in these
            // signatures occupies four stack bytes (int/bool/uint or a pointer).
            if (pointer == IntPtr.Zero && IntPtr.Size == 4)
                pointer = GetProcAddress(library, "_" + name + "@" + (typeof(T).GetMethod("Invoke").GetParameters().Length * 4));
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("설치된 FTP SDK에 필요한 기능이 없습니다: " + name);
            return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
        }
        public int Initialize() { lock (Gate) { if (_initialized) return 0; uint count = 0; int status = _init(ref count); _initialized = status == 0; return status; } }
        public int SetProtocol(int protocol) { lock (Gate) return _protocol(protocol); }
        public int Activation(string ip) { lock (Gate) return _activation(ip); }
        public int Connect(string ip, uint port, string user, string password, ref IntPtr handle, bool encodedPassword)
        { lock (Gate) return _connect(ip, port, user, password, ref handle, encodedPassword); }
        public int Disconnect(IntPtr handle) { lock (Gate) return _disconnect(handle); }
        internal static int WithPath(string path, Func<IntPtr, int> action)
        {
            byte[] data = new UTF8Encoding(false, true).GetBytes(path + "\0");
            var pointer = Marshal.AllocHGlobal(data.Length);
            try { Marshal.Copy(data, 0, pointer, data.Length); return action(pointer); }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public int Delete(IntPtr handle, string path) { lock (Gate) return WithPath(path, p => _delete(handle, p)); }
        public int Rename(IntPtr handle, string from, string to) { lock (Gate) return WithPath(from, f => WithPath(to, t => _rename(handle, f, t))); }
        internal static FtpFileRecord Decode(FileRecord record)
        {
            return new FtpFileRecord { Name = Text(record.FileName), Permissions = Text(record.FilePermission), Links = record.cFileHardLink,
                Size = record.FileSize, Modified = Convert.ToBase64String(record.FileTime ?? new byte[0]) };
        }
        private static string Text(byte[] bytes)
        { if (bytes == null) return ""; int n = Array.IndexOf(bytes, (byte)0); return new UTF8Encoding(false, true).GetString(bytes, 0, n < 0 ? bytes.Length : n); }
        public IList<FtpFileRecord> List(IntPtr handle, string directory)
        {
            lock (Gate)
            {
                var result = new List<FtpFileRecord>();
                WithPath(directory, path => {
                    int count = 0; FtpRecipeSession.Check(_count(handle, path, ref count), "폴더 항목 수 조회");
                    if (count < 0 || count > 10000) throw new InvalidOperationException("카메라 폴더의 항목 수가 잘못되었거나 10,000개를 초과했습니다.");
                    if (count == 0) return 0;
                    int stride = Marshal.SizeOf(typeof(FileRecord));
                    IntPtr entries = Marshal.AllocHGlobal(checked(count * stride)), header = IntPtr.Zero;
                    try
                    {
                        header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileList)));
                        Marshal.StructureToPtr(new FileList { FTPFileInfos = entries, FileNum = count }, header, false);
                        FtpRecipeSession.Check(_list(handle, path, header, false), "폴더 목록 조회");
                        var returned = (FileList)Marshal.PtrToStructure(header, typeof(FileList));
                        if (returned.FileNum < 0 || returned.FileNum > count || returned.FTPFileInfos != entries)
                            throw new InvalidOperationException("조회 중 폴더 목록이 변경되었거나 응답이 잘못되었습니다. 새로고침하세요.");
                        for (int i = 0; i < returned.FileNum; i++) result.Add(Decode((FileRecord)Marshal.PtrToStructure(IntPtr.Add(entries, i * stride), typeof(FileRecord))));
                    }
                    finally { if (header != IntPtr.Zero) Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(entries); }
                    return 0;
                });
                return result;
            }
        }
    }
}
