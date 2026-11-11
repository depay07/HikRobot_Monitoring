using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SC6000DualMonitor;
using VM.Core;
using VM.PlatformSDKCS;
class CalibrationInventoryTests
{
    static int checks;
    static void Check(bool x,string message){if(!x)throw new Exception(message);checks++;}
    static void Reject(Action action,string message){try{action();}catch(InvalidOperationException){checks++;return;}throw new Exception(message);}
    static byte[] Bytes(string s,int size){var b=new byte[size];Encoding.UTF8.GetBytes(s).CopyTo(b,0);return b;}
    static ImvsSdkDefine.IMVS_MODULE_IO_INFO Info(string name,string type="float",string extra=""){
        return new ImvsSdkDefine.IMVS_MODULE_IO_INFO{nDataType=1,szParamName=Bytes(name,128),szParamType=Bytes(type,128),szExtraData=Bytes(extra,512)};
    }
    static CalibrationOutput[] Inventory(params ImvsSdkDefine.IMVS_MODULE_IO_INFO[] items){
        int stride=Marshal.SizeOf(typeof(ImvsSdkDefine.IMVS_MODULE_IO_INFO));
        var list=new ImvsSdkDefine.IMVS_MODULE_IO_INFO_LIST{nNum=items.Length,pstModuIoInfo=Marshal.AllocHGlobal(stride*items.Length)};
        try{for(int i=0;i<items.Length;i++)Marshal.StructureToPtr(items[i],IntPtr.Add(list.pstModuIoInfo,stride*i),false);return CalibrationDeviceData.Decode(list);}
        finally{Marshal.FreeHGlobal(list.pstModuIoInfo);list.pstModuIoInfo=IntPtr.Zero;GC.SuppressFinalize(list);}
    }
    static void Run(){
        var native=Inventory(Info("L2LAbsDist"),Info("CircleRadius"),Info("%새_계수%"));
        Check(native.Select(v=>v.Name).SequenceEqual(new[]{"L2LAbsDist","CircleRadius","%새_계수%"}),"native UTF8 names decoded without template cache");
        Check(CalibrationDeviceData.InventorySignature(native)==CalibrationDeviceData.InventorySignature(native.Reverse()),"inventory order independent");
        Check(CalibrationDeviceData.InventorySignature(native)!=CalibrationDeviceData.InventorySignature(Inventory(Info("L2LAbsDist","int"))),"type change detected");
        Reject(()=>Inventory(Info("x"),Info("x")),"duplicate output rejected");
        Reject(()=>CalibrationDeviceData.Decode(new ImvsSdkDefine.IMVS_MODULE_IO_INFO_LIST{nNum=1}),"missing native pointer rejected");
        Reject(()=>CalibrationDeviceData.Text(null),"missing native string rejected");
        var link=new ImvsSdkDefine.IMVS_SUBSCRIBE_INFO{nSubModuleID=13,nPubModuleID=13000,strPubOutput=Bytes("%scale1%",256),strIndex=Bytes("0",256),nIsIndexConstant=1};
        Check(CalibrationDeviceData.ScalarLink(link,13,13000,"scale1"),"named global scalar input");
        Check(!CalibrationDeviceData.ScalarLink(link,14,13000,"scale1"),"different calculator rejected");
        Check(!CalibrationDeviceData.ScalarLink(link,13,13000,"other"),"different global rejected");
        link.strIndex=Bytes("1",256);Check(!CalibrationDeviceData.ScalarLink(link,13,13000,"scale1"),"array index one rejected");
        link.strIndex=Bytes("All",256);Check(CalibrationDeviceData.ScalarLink(link,13,13000,"scale1"),"all of scalar accepted");
        link.nIsPubConstant=1;Check(!CalibrationDeviceData.ScalarLink(link,13,13000,"scale1"),"constant rejected");
        link.nIsPubConstant=0;link.nIsIndexConstant=0;Check(!CalibrationDeviceData.ScalarLink(link,13,13000,"scale1"),"variable index rejected");
        var global=typeof(GlobalVariableModuleCs.GlobalVariableModuleTool);
        Check(global.GetMethod("SetGlobalVar",new[]{typeof(string),typeof(string)})!=null,"public global setter in installed SDK");
        Check(global.GetMethod("GetVarFloat",new[]{typeof(string)})!=null,"typed float readback in installed SDK");
        Check(global.GetMethod("GetAllGlobalVar",Type.EmptyTypes)!=null,"serialization inventory in installed SDK");
        Console.WriteLine("PASS: "+checks+" installed SDK global API and native inventory checks (no camera calls)");
    }
    static int Main(string[] args){
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(args[0],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try{Run();return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
