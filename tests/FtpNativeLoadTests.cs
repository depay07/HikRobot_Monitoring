using System;
using System.IO;
using System.Collections.Generic;
using SC6000DualMonitor;
class FtpNativeLoadTests
{
    // Only protocol selection reaches the installed DLL. No native Initialize,
    // activation, login, query or mutation is called; all transport is simulated.
    sealed class LocalProtocolApi : ICameraFtpApi
    {
        readonly ICameraFtpApi native;
        public int Protocol, ReturnValue, Logins, Lists, Disconnects;
        public LocalProtocolApi(ICameraFtpApi native) { this.native=native; }
        public int Initialize() { return 0; }
        public int SetProtocol(int protocol) { Protocol=protocol; return ReturnValue=native.SetProtocol(protocol); }
        public int Activation(string ip) { return 0; }
        public int Connect(string ip,uint port,string user,string password,ref IntPtr handle,bool encodedPassword)
        { Logins++;handle=new IntPtr(1);return 0; }
        public int Disconnect(IntPtr handle) { Disconnects++;return 0; }
        public IList<FtpFileRecord> List(IntPtr handle,string directory)
        { Lists++;return new List<FtpFileRecord>(); }
        public int Delete(IntPtr handle,string path) { throw new Exception("Unexpected delete"); }
        public int Rename(IntPtr handle,string from,string to) { throw new Exception("Unexpected rename"); }
    }
    static int Main(string[] args)
    {
        try
        {
            string architecture=IntPtr.Size==8?"x64":"x86";
            string sdk=Path.Combine(args[0],"VM.Core.dll");
            string library=NativeCameraFtp.FindLibrary(sdk);
            string suffix=IntPtr.Size==8?@"Applications\PublicFile\x64\FTPClientSDK.dll":@"Applications\Tools\FileTranferTool\FTPClientSDK.dll";
            if(!library.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))throw new Exception("Wrong architecture SDK path");
            var api=new LocalProtocolApi(NativeCameraFtp.Open(sdk));
            const string folder="/root/vmtempfiles/ftp/solution";
            using(var session=new FtpRecipeSession("unused.invalid","test-only",()=>folder+"/active.solx",()=>api,null))
            {
                session.List(folder);
                if(api.Protocol!=2||api.Logins!=1||api.Lists!=1)throw new Exception("Native protocol return incorrectly blocked simulated login/list");
            }
            if(api.Disconnects!=1)throw new Exception("Simulated session not released");
            Console.WriteLine("PASS: "+architecture+" native exports and actual FTP_SetProtocol(FTPES) through session; undefined return 0x"+api.ReturnValue.ToString("X8")+" ignored (no camera calls)");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
