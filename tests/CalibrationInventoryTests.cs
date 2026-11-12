using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
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
    sealed class NativeTree : IDisposable
    {
        readonly List<IntPtr> buffers=new List<IntPtr>();
        public ImvsSdkDefine.IMVS_MODULE_IO_INFO Group(string name, params ImvsSdkDefine.IMVS_MODULE_IO_INFO[] children)
        {
            var group=Info(name,"ROIANNULUS");group.nDataType=2;group.nNum=children.Length;
            if(children.Length>0){int stride=Marshal.SizeOf(typeof(ImvsSdkDefine.IMVS_MODULE_IO_INFO));group.pstCombInfo=Marshal.AllocHGlobal(stride*children.Length);buffers.Add(group.pstCombInfo);for(int i=0;i<children.Length;i++)Marshal.StructureToPtr(children[i],IntPtr.Add(group.pstCombInfo,stride*i),false);}
            return group;
        }
        public ImvsSdkDefine.IMVS_MODULE_IO_INFO FromXml(XmlNode node)
        {
            if(node.Name=="Combination")return Group(node.Attributes["Name"].Value,node.SelectNodes("Filters/*").Cast<XmlNode>().Select(FromXml).ToArray());
            return Info(node.Attributes["Name"].Value,node.Attributes["ValueType"].Value);
        }
        public void Dispose(){foreach(var p in buffers)Marshal.FreeHGlobal(p);}
    }
    static void NestedOutputs(string sdk)
    {
        var document=new XmlDocument();document.Load(Path.Combine(sdk,"Module(sp)","x64","Location","IMVSCircleFindModu","IMVSCircleFindModu.xml"));
        CalibrationOutput[] actual;
        using(var memory=new NativeTree())actual=Inventory(document.SelectNodes("//Category[@Name='Output']/Items/*").Cast<XmlNode>().Select(memory.FromXml).ToArray());
        Check(!actual.Any(x=>x.Name=="CircleRadius"),"installed circle definition hides radius inside a composite; old top-level scan misses it");
        Check(CalibrationDeviceData.Measurements(actual).Select(x=>x.Name).SequenceEqual(new[]{"CircleRadius"}),"real installed circle hierarchy discovers exactly the outer radius");
        Check(CalibrationDeviceData.Leaves(actual).Any(x=>x.Name=="CircleCenterX"),"multiple nested levels copied, usable after native buffers freed");
        Check(CalibrationDeviceData.Measurements(Inventory(Info("L2LAbsDist"))).Single().Name=="L2LAbsDist","flat line measurement still selected");
        Check(CalibrationDeviceData.Measurements(Inventory(Info("CircleRadius","int"))).Length==0,"non-float radius not offered");
        using(var memory=new NativeTree())
        {
            var tree=Inventory(memory.Group("A",Info("X")),memory.Group("B",Info("X")));
            Check(CalibrationDeviceData.Leaves(tree).Count()==2,"same leaf names in separate groups allowed");
            var before=Inventory(memory.Group("A",Info("CircleRadius")));
            var after=Inventory(memory.Group("A",Info("CircleInnerRadius")));
            Check(CalibrationDeviceData.InventorySignature(before)!=CalibrationDeviceData.InventorySignature(after),"nested binding changes reflected in signature");
            Reject(()=>CalibrationDeviceData.Measurements(Inventory(memory.Group("A",Info("CircleRadius")),memory.Group("B",Info("CircleRadius")))),"ambiguous measurement leaves rejected");
            var broken=memory.Group("broken");broken.nNum=1;Reject(()=>Inventory(broken),"missing nested pointer rejected");
            var cycle=memory.Group("cycle",Info("placeholder"));Marshal.StructureToPtr(cycle,cycle.pstCombInfo,false);Reject(()=>Inventory(cycle),"cyclic child pointer rejected");
            var deep=Info("CircleRadius");for(int i=0;i<34;i++)deep=memory.Group("group"+i,deep);Reject(()=>Inventory(deep),"excessive nesting bounded");
        }
    }
    static void Run(string sdk){
        NestedOutputs(sdk);

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
        var globalInfo=new ImvsSdkDefine.IMVS_MODULE_INFO{nNodeID=13000,strModuleName=Bytes("GlobalVariableModule",64),strDisplayName=Bytes("Renamed global",256)};
        var globals=new ImvsSdkDefine.IMVS_MODULE_INFO_LIST{nNum=1,astModuleInfo=new[]{globalInfo}};
        Check(CalibrationDeviceData.GlobalVariables(globals).Single().nNodeID==13000,"dedicated global inventory resolves module without ordinary module list");
        globalInfo.strDisplayName=Bytes("새 전역 이름",256); globals.astModuleInfo[0]=globalInfo;
        Check(CalibrationDeviceData.GlobalVariables(globals).Length==1,"renamed display name does not affect type discovery");
        Reject(()=>CalibrationDeviceData.GlobalVariables(null),"unavailable inventory is not missing globals");
        Reject(()=>CalibrationDeviceData.GlobalVariables(new ImvsSdkDefine.IMVS_MODULE_INFO_LIST{nNum=1}),"truncated global inventory rejected");
        Check(CalibrationDeviceData.GlobalVariables(new ImvsSdkDefine.IMVS_MODULE_INFO_LIST{nNum=0}).Length==0,"valid empty inventory distinguished");
        Reject(()=>CalibrationDeviceData.GlobalVariables(new ImvsSdkDefine.IMVS_MODULE_INFO_LIST{nNum=2,astModuleInfo=new[]{globalInfo,globalInfo}}),"duplicate global identity rejected");
        globalInfo.strModuleName=Bytes("GlobalScriptModule",64); globals.astModuleInfo[0]=globalInfo;
        Check(CalibrationDeviceData.GlobalVariables(globals).Length==0,"global script is not global variable");
        var global=typeof(GlobalVariableModuleCs.GlobalVariableModuleTool);
        Check(global.GetMethod("SetGlobalVar",new[]{typeof(string),typeof(string)})!=null,"public global setter in installed SDK");
        Check(global.GetMethod("GetVarFloat",new[]{typeof(string)})!=null,"typed float readback in installed SDK");
        Check(global.GetMethod("GetAllGlobalVar",Type.EmptyTypes)!=null,"serialization inventory in installed SDK");
        Console.WriteLine("PASS: "+checks+" installed SDK global API and native inventory checks (no camera calls)");
    }
    static int Main(string[] args){
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(args[0],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try{Run(args[0]);return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
