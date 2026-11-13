using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
class RecipeFileSdkTests
{
    static int checks;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    static byte[] Bytes(string s){var b=new byte[260];Encoding.UTF8.GetBytes(s).CopyTo(b,0);return b;}
    static int Main(string[] args)
    {
        try
        {
            string folder=Path.GetFullPath(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            var sdk=Assembly.LoadFrom(Path.Combine(folder,"VM.PlatformSDKCS.dll"));var app=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var type=sdk.GetType("VM.PlatformSDKCS.ImvsSdkDefine+FTP_REMOTE_FILE_INFO");object file=Activator.CreateInstance(type);
            Action<string,object> set=(n,v)=>type.GetField(n).SetValue(file,v);
            set("FileName",Bytes("한글 제품.solx"));set("FilePermission",Bytes("-rw-r--r--"));set("FileSize",1234);set("cFileHardLink",1);
            set("FileTime",new byte[]{255,254,0}); // A locale-specific timestamp must not break an otherwise valid listing.
            var decode=app.GetType("SC6000DualMonitor.RemoteSolutionFiles").GetMethod("DecodeFile",BindingFlags.Static|BindingFlags.NonPublic);
            Func<bool,object> read=restricted=>decode.Invoke(null,new object[]{file,"/root/solution",restricted});
            var entry=read(true);Check(entry!=null,"regular SDK record accepted");
            Check((string)entry.GetType().GetField("Path").GetValue(entry)=="/root/solution/한글 제품.solx","UTF8 Korean file name intact");
            Check((long)entry.GetType().GetField("Size").GetValue(entry)==1234,"native file size retained");
            Check(read(false)!=null,"legacy solution chooser still accepts valid record");
            set("FilePermission",Bytes("drwxr-xr-x"));Check(read(true)==null&&read(false)==null,"directory never offered as recipe");
            set("FilePermission",Bytes("lrwxrwxrwx"));Check(read(true)==null,"manager excludes symlink");Check(read(false)!=null,"legacy list behavior preserved for links");
            set("FilePermission",Bytes(""));Check(read(true)==null,"unknown type fails closed for manager");
            set("FilePermission",Bytes("-rw-r--r--"));set("cFileHardLink",2);Check(read(true)==null,"hard-linked recipe excluded");
            set("cFileHardLink",1);set("FileName",Bytes("settings.ini"));Check(read(true)==null,"nonrecipe excluded");
            set("FileName",Bytes("../active.solx"));bool rejected=false;try{read(true);}catch(TargetInvocationException){rejected=true;}Check(rejected,"path injection in server listing rejected");
            var adapter=app.GetType("SC6000DualMonitor.NativeCameraFtp");
            var toolFolder=Path.GetFullPath(Path.Combine(folder,@"..\..\..\..\Applications\Tools\FileTranferTool"));
            var ftp=Assembly.LoadFrom(Path.Combine(toolFolder,"FTPClientSdkServer.dll"));
            foreach(var pair in new[]{new[]{"FileRecord","FTP_REMOTE_FILE_INFO"},new[]{"FileList","FTP_REMOTE_FILE_LIST_INFO"}})
            {
                var local=adapter.GetNestedType(pair[0],BindingFlags.NonPublic);
                var vendor=ftp.GetType("FTPClientSdkServer.FTPClientData+"+pair[1]);
                Check(Marshal.SizeOf(local)==Marshal.SizeOf(vendor),"native struct size "+pair[0]);
                foreach(var field in vendor.GetFields()) Check(Marshal.OffsetOf(local,field.Name)==Marshal.OffsetOf(vendor,field.Name),"native field offset "+field.Name);
            }
            foreach(var pair in new[]{new[]{"InitCall","FTP_Init"},new[]{"ProtocolCall","FTP_SetProtocol"},new[]{"ActivationCall","FTP_CheckDeviceActivationStatus"},new[]{"ConnectCall","FTP_ConnectTCPIP"},new[]{"DisconnectCall","FTP_Disconnect"},new[]{"CountCall","FTP_RemoteFileCount"},new[]{"ListCall","FTP_RemoteFileList"},new[]{"DeleteCall","FTP_RemoveFile"},new[]{"RenameCall","FTP_Rename"}})
            {
                var delegateType=adapter.GetNestedType(pair[0],BindingFlags.NonPublic);
                var actual=(UnmanagedFunctionPointerAttribute)Attribute.GetCustomAttribute(delegateType,typeof(UnmanagedFunctionPointerAttribute));
                var method=ftp.GetType("FTPClientSdkServer.NativeMethods").GetMethod(pair[1]);
                var expected=(DllImportAttribute)Attribute.GetCustomAttribute(method,typeof(DllImportAttribute));
                Check(actual.CallingConvention==expected.CallingConvention && delegateType.GetMethod("Invoke").GetParameters().Length==method.GetParameters().Length,"native calling convention/count "+pair[1]);
            }
            var withPath=adapter.GetMethod("WithPath",BindingFlags.Static|BindingFlags.NonPublic);
            string path="/root/vmtempfiles/ftp/solution/한글 제품.solx";
            Func<IntPtr,int> inspect=p=>{var bytes=new byte[Encoding.UTF8.GetByteCount(path)+1];Marshal.Copy(p,bytes,0,bytes.Length);Check(bytes[bytes.Length-1]==0&&Encoding.UTF8.GetString(bytes,0,bytes.Length-1)==path,"UTF8 nul-terminated mutation path");return 7;};
            Check((int)withPath.Invoke(null,new object[]{path,inspect})==7,"native error status preserved");
            var found=(string)adapter.GetMethod("FindLibrary",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{Path.Combine(folder,"VM.Core.dll")});
            Check(string.Equals(found,Path.Combine(toolFolder,"FTPClientSDK.dll"),StringComparison.OrdinalIgnoreCase),"use tool DLL from same VisionMaster install");
            // Load library and resolve all exports only. Do NOT call Initialize, Connect or any camera operation.
            Check(adapter.GetMethod("Open",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{Path.Combine(folder,"VM.Core.dll")})!=null,"installed native DLL/dependencies load and all required exports bind");
            Console.WriteLine("PASS: "+checks+" installed SDK FTP metadata, UTF8 and native contract checks (no camera calls)");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
