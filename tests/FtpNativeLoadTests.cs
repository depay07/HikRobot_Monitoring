using System;
using System.IO;
using SC6000DualMonitor;
class FtpNativeLoadTests
{
    static int Main(string[] args)
    {
        try
        {
            if(IntPtr.Size!=8)throw new Exception("Expected x64 test process");
            string sdk=Path.Combine(args[0],"VM.Core.dll");
            string library=NativeCameraFtp.FindLibrary(sdk);
            if(!library.EndsWith(@"Applications\PublicFile\x64\FTPClientSDK.dll",StringComparison.OrdinalIgnoreCase))throw new Exception("Wrong architecture SDK path");
            // Bind exports only: no Initialize, login or camera command.
            if(NativeCameraFtp.Open(sdk)==null)throw new Exception("Native SDK not loaded");
            Console.WriteLine("PASS: x64 SDK path, dependencies and 9 native function exports (no camera calls)");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
